using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Filters;

/// <summary>
/// Shows, hides, installs and removes post-processing filters.
/// </summary>
/// <remarks>
/// ⚠️ Every write here goes through "load the live table, remove only our own rows, put them back".
/// The reference implementation copies <c>post_processing.table.bak</c> over the live file first,
/// and that is the same stale-snapshot pattern documented in <see cref="FlatPad.Installer"/> —
/// which once wrote back a copy taken before a game update and deleted Kyalami's registration. Here
/// it would additionally delete every row belonging to any other tool. This class never creates a
/// backup file and never reads one.
/// </remarks>
public sealed class FilterInstaller(string gameRoot, Action<string> log, IStockRegistry? stock = null)
{
    private string Rp(string reference) => RefPath.RealPath(gameRoot, reference);

    /// <summary>What the table and the disk currently say.</summary>
    public FilterSurvey Survey(IEnumerable<FilterEntry>? ours = null) =>
        FilterStates.Detect(gameRoot, ours, stock);

    /// <summary>Offer these filters in the video options. Returns how many rows changed.</summary>
    public int Show(IEnumerable<string> names) => SetVisibility(names, visible: true);

    /// <summary>Take these back out of the video options. Returns how many rows changed.</summary>
    public int Hide(IEnumerable<string> names) => SetVisibility(names, visible: false);

    /// <summary>Show every filter the game registers but never offers.</summary>
    public int ShowShippedHidden()
    {
        List<string> names = [.. Survey().ShippedHidden.Select(f => f.Name)];
        log($"Showing {names.Count} filter(s) the game ships hidden:");
        int changed = Show(names);
        log(changed == 0
            ? "  nothing to do — they are all shown already"
            : $"  post_processing.table: {changed} row(s) now offered");
        return changed;
    }

    /// <summary>Put the shipped-hidden filters back the way the game ships them.</summary>
    public int RestoreShippedHidden()
    {
        List<string> names = [.. Survey().ShippedHidden.Select(f => f.Name)];
        log($"Hiding {names.Count} filter(s) the game ships hidden:");
        int changed = Hide(names);
        log(changed == 0
            ? "  nothing to do — they are hidden already"
            : $"  post_processing.table: {changed} row(s) hidden again");
        return changed;
    }

    /// <summary>Write a bundle's filters into the game and register them. Returns how many.</summary>
    /// <remarks>
    /// Idempotent by construction: our own rows are dropped and re-added rather than detected, so a
    /// second run leaves one row per filter and a run after a game patch simply puts back what the
    /// patch removed. Files are written only after the whole plan resolves, so a missing dependency
    /// leaves the install exactly as it was.
    /// </remarks>
    public int Install(IFilterBundle bundle, IEnumerable<FilterEntry>? only = null)
    {
        List<FilterEntry> entries = [.. only ?? bundle.Filters];
        if (entries.Count == 0)
            return 0;

        HashSet<string> shipped = ShippedNames();
        foreach (FilterEntry entry in entries.Where(e => shipped.Contains(e.Name)))
        {
            throw new InstallException(
                $"'{entry.Name}' is a name the game already ships. Registering a second row under it " +
                "would leave two filters the menu cannot tell apart.");
        }

        log($"Installing {entries.Count} filter(s) from {bundle.Describe}:");

        var game = new GameAssets(gameRoot, stock);
        FilterPlan plan = FilterPlanner.Build(bundle, entries, game);

        foreach ((string reference, byte[] bytes) in plan.Writes)
        {
            string real = Rp(reference);
            Directory.CreateDirectory(Path.GetDirectoryName(real)!);
            File.WriteAllBytes(real, bytes);
        }

        log($"  files: {plan.Writes.Count} written, {plan.Resolved.Count} reference(s) already present");

        string table = Rp(FilterSpec.PpTable);
        List<PbNode> tree = PbTree.ParseTree(File.ReadAllBytes(table));
        var ours = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        // Never a snapshot restore. Load what is there, drop only rows carrying our own names, and
        // put them back — so another tool's rows, and anything a game update added, survive intact.
        int replaced = FilterTable.RemoveRows(tree, r => ours.Contains(r.Name) && !shipped.Contains(r.Name));

        (_, List<FilterRow> rows) = FilterTable.Read(tree);
        FilterRow template = FilterTable.Template(rows, ours);
        foreach (FilterEntry entry in entries)
            FilterTable.AppendRow(tree, template, entry.Name, entry.InstallRef);

        File.WriteAllBytes(table, PbTree.EncodeTree(tree));
        log($"  post_processing.table: {entries.Count} registered" +
            (replaced > 0 ? $", replacing {replaced} already there" : ""));

        return entries.Count;
    }

    /// <summary>Drop a bundle's rows and delete its folders. Returns how many rows went.</summary>
    /// <remarks>
    /// A hand-written inverse, not a restore, for the same reason the Flat Pad uninstall is one.
    /// Where it is not certain a file is ours, it stays: an orphaned 203-byte curve is harmless, and
    /// deleting something we cannot prove we planted is the mistake this project already learned not
    /// to make.
    /// </remarks>
    public int Uninstall(IFilterBundle bundle, IEnumerable<FilterEntry>? only = null)
    {
        List<FilterEntry> entries = [.. only ?? bundle.Filters];
        if (entries.Count == 0)
            return 0;

        log($"Removing {entries.Count} filter(s):");

        HashSet<string> shipped = ShippedNames();
        HashSet<string> stockFolders = StockFolders(shipped);
        var ourFolders = entries.Select(e => e.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Read what they point at before deleting anything, so what is left behind can be named.
        var outside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FilterEntry entry in entries)
        {
            string real = Rp(entry.InstallRef);
            if (!File.Exists(real))
                continue;
            foreach (string curve in FilterPlanner.CurveRefs(File.ReadAllBytes(real)))
            {
                string folder = curve.Split('/').SkipLast(1).LastOrDefault() ?? "";
                if (!ourFolders.Contains(folder))
                    outside.Add(curve);
            }
        }

        string table = Rp(FilterSpec.PpTable);
        int removed = 0;
        if (File.Exists(table))
        {
            List<PbNode> tree = PbTree.ParseTree(File.ReadAllBytes(table));
            var ours = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            removed = FilterTable.RemoveRows(tree, r => ours.Contains(r.Name) && !shipped.Contains(r.Name));
            if (removed > 0)
                File.WriteAllBytes(table, PbTree.EncodeTree(tree));
        }

        log($"  post_processing.table: {removed} row(s) dropped");

        int deleted = 0;
        foreach (FilterEntry entry in entries)
        {
            if (stockFolders.Contains(entry.Folder))
            {
                log($"  left {entry.Folder} alone — the game ships a filter out of it");
                continue;
            }

            string dir = Rp($"{FilterSpec.PpDir}/{entry.Folder}");
            if (!Directory.Exists(dir))
                continue;
            Directory.Delete(dir, recursive: true);
            deleted++;
        }

        log($"  files: {deleted} folder(s) deleted");
        if (outside.Count > 0)
            log($"  left {outside.Count} referenced file(s) outside those folders, which may not be ours");

        return removed;
    }

    /// <summary>Names the game itself registers — from the archive, or the shipped list.</summary>
    private HashSet<string> ShippedNames()
    {
        IStockRegistry registry = stock ?? ArchiveStockRegistry.ForGame(gameRoot);
        if (registry.Available)
        {
            try
            {
                if (registry.Read(FilterSpec.PpTable) is { } bytes)
                {
                    var names = FilterTable.Read(bytes).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
                    if (names.Count > 0)
                        return names;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                           or ProtobufException or InvalidDataException)
            {
                // Fall through to the shipped list.
            }
        }

        return [.. FilterSpec.Shipped];
    }

    /// <summary>
    /// Folders a stock row points into, read off the live table so this works with no archive.
    /// </summary>
    private HashSet<string> StockFolders(HashSet<string> shipped)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (FilterRow row in FilterTable.Read(File.ReadAllBytes(Rp(FilterSpec.PpTable))))
            {
                if (!shipped.Contains(row.Name))
                    continue;
                string canon = RefPath.Canon(row.Path);
                if (canon.Split('/').SkipLast(1).LastOrDefault() is { Length: > 0 } folder)
                    folders.Add(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ProtobufException or InvalidDataException)
        {
            // No table to read means nothing to protect from a folder delete that will also find
            // nothing. The per-entry Directory.Exists check below is the real guard.
        }

        return folders;
    }

    private int SetVisibility(IEnumerable<string> names, bool visible)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return 0;

        string table = Rp(FilterSpec.PpTable);
        List<PbNode> tree = PbTree.ParseTree(File.ReadAllBytes(table));
        (PbNode root, List<FilterRow> rows) = FilterTable.Read(tree);

        int changed = 0;
        foreach (FilterRow row in rows.Where(r => wanted.Contains(r.Name)))
        {
            if (FilterTable.SetVisible(root, row, visible))
                changed++;
        }

        // Writing an identical file would still bump its timestamp, which is the sort of thing that
        // makes a later "what changed?" harder to answer than it needs to be.
        if (changed > 0)
            File.WriteAllBytes(table, PbTree.EncodeTree(tree));

        return changed;
    }
}
