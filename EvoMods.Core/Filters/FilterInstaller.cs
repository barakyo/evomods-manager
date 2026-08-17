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
