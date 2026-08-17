using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Filters;

/// <summary>
/// Reports what is registered, what is offered, and what would silently fail to load.
/// </summary>
/// <remarks>
/// Produces its own <see cref="VerifyReport"/> alongside <see cref="Verifier"/> rather than adding
/// to it: they check unrelated things and the Flat Pad report is already long. That report's
/// <see cref="VerifyReport.Registry"/> is track-specific and stays null here — left alone rather
/// than generalised, because nothing yet needs it to be.
/// <para>
/// Every check reports a COUNT. A check that quietly found nothing to check would otherwise
/// contribute a cheerful and meaningless pass.
/// </para>
/// </remarks>
public sealed class FilterVerifier(
    string gameRoot, IFilterBundle bundle, IStockRegistry? stock = null)
{
    private string Rp(string reference) => RefPath.RealPath(gameRoot, reference);

    public VerifyReport Run()
    {
        var report = new VerifyReport();
        report.Line("Verifying post-processing filters:");

        FilterSurvey survey = FilterStates.Detect(gameRoot, bundle.Filters, stock);
        if (survey.Filters.Count == 0)
        {
            report.Problem($"post_processing.table could not be read — {survey.StockSource}");
            return report;
        }

        CheckRows(report, survey);
        CheckVisibility(report, survey);
        CheckOurs(report, survey);
        CheckDependencies(report, survey);
        CheckNames(report, survey);
        CheckLeftoverBackups(report);
        return report;
    }

    private static void CheckRows(VerifyReport report, FilterSurvey survey)
    {
        int stock = survey.Filters.Count(f => f.State
            is FilterState.StockShown or FilterState.StockHidden
            or FilterState.StockUnhidden or FilterState.StockSuppressed);
        int ours = survey.Filters.Count(f => f.State
            is FilterState.Installed or FilterState.RegisteredButFileMissing);
        int other = survey.Filters.Count(f => f.State == FilterState.Foreign);

        report.Line($"  rows: {stock + ours + other} ({stock} stock, {ours} ours, {other} other)");
    }

    private static void CheckVisibility(VerifyReport report, FilterSurvey survey)
    {
        int hidden = survey.Filters.Count(f => f.State is FilterState.StockHidden);
        int unhidden = survey.Filters.Count(f => f.State is FilterState.StockUnhidden);
        int suppressed = survey.Filters.Count(f => f.State is FilterState.StockSuppressed);

        report.Line($"  shipped hidden: {hidden + unhidden} ({unhidden} shown here) — "
            + (survey.StockAvailable ? $"compared against {survey.StockSource}" : survey.StockSource));

        if (suppressed > 0)
        {
            // Not raised as a problem: it is not ours, and something else may want it that way.
            report.Line($"  {suppressed} filter(s) the game ships visible are hidden in this install");
        }
    }

    private void CheckOurs(VerifyReport report, FilterSurvey survey)
    {
        var ours = survey.Filters
            .Where(f => bundle.Filters.Any(e => e.Name == f.Name))
            .ToList();

        int installed = ours.Count(f => f.State == FilterState.Installed);
        report.Line($"  {bundle.Describe}: {installed}/{bundle.Filters.Count} installed");

        foreach (FilterStatus f in ours.Where(f => f.State == FilterState.RegisteredButFileMissing))
        {
            report.Problem(
                $"'{f.Name}' is registered but {f.Path} is not there — the game will list it, let it " +
                "be selected, and silently fail to load it");
        }

        int stranded = ours.Count(f => f.State == FilterState.FilesPresentButNotRegistered);
        if (stranded > 0)
        {
            report.Line($"  {stranded} of ours have files but no row — a game patch or a Steam file " +
                "verification does this; installing again puts them back");
        }
    }

    private void CheckDependencies(VerifyReport report, FilterSurvey survey)
    {
        var game = new GameAssets(gameRoot, stock);
        int refs = 0, resolved = 0;

        foreach (FilterStatus f in survey.Filters.Where(f =>
            f.State is FilterState.Installed or FilterState.StockShown
            or FilterState.StockHidden or FilterState.StockUnhidden))
        {
            string real = Rp(f.Path);
            if (!File.Exists(real))
                continue;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(real);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string curve in FilterPlanner.CurveRefs(bytes))
            {
                refs++;
                if (game.Has(curve))
                    resolved++;
                else
                    report.Problem($"'{f.Name}' references {FilterSpec.TablePath(curve)}, which is not there");
            }
        }

        report.Line($"  curve references: {refs} checked, {resolved} resolved");
    }

    private static void CheckNames(VerifyReport report, FilterSurvey survey)
    {
        // Stock names carry spaces legitimately, because en.loc defines them. Anything else with a
        // space is registered, selectable and permanently inert.
        var spaced = survey.Filters
            .Where(f => !FilterSpec.IsLoadableName(f.Name))
            .Where(f => f.State is not (FilterState.StockShown or FilterState.StockHidden
                or FilterState.StockUnhidden or FilterState.StockSuppressed))
            .ToList();

        report.Line($"  names: {spaced.Count} carry a space the game does not define");
        foreach (FilterStatus f in spaced)
        {
            report.Problem(
                $"'{f.Name}' contains a space. The name is a localization key, so the game lists it, " +
                "lets it be selected, and never loads it");
        }
    }

    private void CheckLeftoverBackups(VerifyReport report)
    {
        // Reported, never deleted — they are another tool's, not ours. Worth naming because a later
        // run of that tool copies the .bak back over the live table, which would drop every row a
        // game update had since added.
        string[] leftovers = ["post_processing.table.bak", "post_processing.table.previs"];
        List<string> present = [.. leftovers.Where(n => File.Exists(Rp($"system/{n}")))];

        report.Line($"  leftover backups: {present.Count}"
            + (present.Count > 0 ? $" ({string.Join(", ", present)}) — not ours, left alone" : ""));
    }
}
