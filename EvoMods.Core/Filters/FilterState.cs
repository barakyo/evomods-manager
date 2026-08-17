using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Filters;

/// <summary>What a look at the table and the disk says about one filter.</summary>
/// <remarks>
/// Deliberately more than "is there a row". <see cref="FlatPad.FlatPadState"/> exists because a
/// folder surviving an unpack while its registration did not reports as a healthy install and sends
/// the user to a list that does not contain it. Filters have three ways to be half-there, not one,
/// and two of them are invisible from inside the game.
/// </remarks>
public enum FilterState
{
    /// <summary>Ours, with neither files nor a row.</summary>
    NotInstalled,

    /// <summary>Ours: the file is on disk and a row points at it. The game will offer it.</summary>
    Installed,

    /// <summary>
    /// Ours: the file is on disk, but nothing registers it. A game patch or a Steam file
    /// verification restores the stock table over ours and leaves exactly this — the folder
    /// survives, the registration does not, and nothing anywhere says so.
    /// </summary>
    FilesPresentButNotRegistered,

    /// <summary>
    /// A row points at a <c>.postprocessing</c> that is not there. The filter is listed and
    /// selectable and silently fails to load — the same invisible failure as a name with a space.
    /// </summary>
    RegisteredButFileMissing,

    /// <summary>Stock, and the game offers it. Nothing of ours involved.</summary>
    StockShown,

    /// <summary>Stock, registered without the visible flag, exactly as Kunos ships it.</summary>
    StockHidden,

    /// <summary>Stock, shipped hidden, shown in this install. What "re-hide" acts on.</summary>
    StockUnhidden,

    /// <summary>Stock, shipped visible, hidden in this install. Not ours and not Kunos'. Reported only.</summary>
    StockSuppressed,

    /// <summary>Registered by something that is neither the game nor this app. Left strictly alone.</summary>
    Foreign,
}

/// <param name="Note">Why we believe the stock half, when it did not come from the archive.</param>
public sealed record FilterStatus(string Name, string Path, FilterState State, string? Note = null)
{
    /// <summary>Is this one of the nine the game ships hidden, however it currently reads?</summary>
    public bool IsShippedHidden => State is FilterState.StockHidden or FilterState.StockUnhidden;
}

/// <param name="StockAvailable">False when the stock half came from the shipped fallback list.</param>
public sealed record FilterSurvey(
    bool StockAvailable,
    string StockSource,
    IReadOnlyList<FilterStatus> Filters)
{
    public static FilterSurvey Unreadable(string reason) => new(false, reason, []);

    public IEnumerable<FilterStatus> ShippedHidden => Filters.Where(f => f.IsShippedHidden);
}

/// <summary>Works out what is registered, what is hidden, and what is only half-installed.</summary>
public static class FilterStates
{
    /// <summary>
    /// Survey the install. Never throws — a status check has to answer even when the table is
    /// missing or shaped unexpectedly.
    /// </summary>
    /// <param name="ours">
    /// The filters this app would install. Pass an empty list to describe only what is already
    /// there.
    /// </param>
    public static FilterSurvey Detect(
        string gameRoot, IEnumerable<FilterEntry>? ours = null, IStockRegistry? stock = null)
    {
        List<FilterEntry> mine = [.. ours ?? []];
        List<FilterRow> live;
        try
        {
            live = FilterTable.Read(File.ReadAllBytes(RefPath.RealPath(gameRoot, FilterSpec.PpTable)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ProtobufException or InvalidDataException)
        {
            return FilterSurvey.Unreadable($"post_processing.table could not be read: {e.Message}");
        }

        (Dictionary<string, bool> stockVisible, bool available, string source) = Stock(gameRoot, stock);
        string? note = available ? null : "from the shipped list — no archive to compare against";

        Dictionary<string, FilterEntry> byName = mine.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var statuses = new List<FilterStatus>();

        foreach (FilterRow row in live)
        {
            if (byName.TryGetValue(row.Name, out FilterEntry? entry))
            {
                statuses.Add(new FilterStatus(row.Name, row.Path,
                    OnDisk(gameRoot, entry)
                        ? FilterState.Installed
                        : FilterState.RegisteredButFileMissing));
            }
            else if (stockVisible.TryGetValue(row.Name, out bool shipsVisible))
            {
                statuses.Add(new FilterStatus(row.Name, row.Path, (shipsVisible, row.Visible) switch
                {
                    (true, true) => FilterState.StockShown,
                    (true, false) => FilterState.StockSuppressed,
                    (false, true) => FilterState.StockUnhidden,
                    (false, false) => FilterState.StockHidden,
                }, note));
            }
            else
            {
                statuses.Add(new FilterStatus(row.Name, row.Path, FilterState.Foreign));
            }
        }

        // Ours that no row mentions. The distinction matters: files still there after a patch wiped
        // the table is a one-click fix, and a fresh install is not.
        var registered = live.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (FilterEntry entry in mine.Where(e => !registered.Contains(e.Name)))
        {
            statuses.Add(new FilterStatus(entry.Name, FilterSpec.TablePath(entry.InstallRef),
                OnDisk(gameRoot, entry)
                    ? FilterState.FilesPresentButNotRegistered
                    : FilterState.NotInstalled));
        }

        return new FilterSurvey(available, source, statuses);
    }

    private static bool OnDisk(string gameRoot, FilterEntry entry) =>
        File.Exists(RefPath.RealPath(gameRoot, entry.InstallRef));

    /// <summary>
    /// Which names the game ships, and whether it offers them — read from the archive where possible.
    /// </summary>
    /// <remarks>
    /// The fallback is a list in the source, not a snapshot on the user's disk, and the difference
    /// matters. The pattern this project rejects is a whole-file copy taken on a machine that then
    /// goes stale across a game update and gets written back over a registry the update had grown.
    /// A constant is versioned with the tool and reviewable; its worst case is offering to re-hide a
    /// filter some future patch decided to show, which is one row, visible, user-initiated and
    /// reversible.
    /// </remarks>
    private static (Dictionary<string, bool> Visible, bool Available, string Source) Stock(
        string gameRoot, IStockRegistry? stock)
    {
        stock ??= ArchiveStockRegistry.ForGame(gameRoot);
        if (stock.Available)
        {
            try
            {
                if (stock.Read(FilterSpec.PpTable) is { } bytes)
                {
                    Dictionary<string, bool> visible = FilterTable.Read(bytes)
                        .GroupBy(r => r.Name, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().Visible, StringComparer.Ordinal);
                    if (visible.Count > 0)
                        return (visible, true, stock.Describe);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                           or ProtobufException or InvalidDataException)
            {
                // Fall through to the shipped list rather than failing a status check.
            }
        }

        Dictionary<string, bool> fallback = FilterSpec.Shipped.ToDictionary(
            n => n,
            n => !FilterSpec.ShippedHidden.Contains(n, StringComparer.Ordinal),
            StringComparer.Ordinal);
        return (fallback, false, "the shipped list");
    }
}
