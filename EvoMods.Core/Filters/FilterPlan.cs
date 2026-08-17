using EvoMods.Core.FlatPad;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Filters;

/// <summary>Everything an install would write, worked out before anything is written.</summary>
/// <param name="Writes">Canonical reference to the bytes that should be at it.</param>
/// <param name="Resolved">References the game already has, on disk or in its archive.</param>
public sealed record FilterPlan(
    IReadOnlyDictionary<string, byte[]> Writes,
    IReadOnlyCollection<string> Resolved)
{
    public IEnumerable<string> Curves => Writes.Keys.Where(FilterSpec.IsCurveRef);
}

/// <summary>
/// Works out which files a set of filters needs, by reading what they actually reference.
/// </summary>
/// <remarks>
/// ⚠️ Derived, never declared. A <c>.postprocessing</c> names its curves as strings, and in the live
/// schema those <c>curvePath</c> fields are the ONLY strings there are — so
/// <see cref="PbTree.WalkStrings"/> finds them all with no schema at all, and the extension filter is
/// belt and braces. A bundle that declared its own dependencies could be wrong about them; one that
/// is read cannot be. See <see cref="IFilterBundle"/> for the bug in the reference implementation
/// that this shape makes unrepresentable.
/// </remarks>
public static class FilterPlanner
{
    /// <summary>Curve references a filter names, canonical and deduplicated.</summary>
    public static IEnumerable<string> CurveRefs(byte[] filter) =>
        PbTree.WalkStrings(filter)
            .Select(hit => RefPath.Canon(hit.Text))
            .Where(FilterSpec.IsCurveRef)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Plan an install. Throws before returning if anything a filter needs cannot be found, so a
    /// caller never gets a plan that would leave a half-installed filter behind.
    /// </summary>
    public static FilterPlan Build(
        IFilterBundle bundle, IEnumerable<FilterEntry> entries, IGameAssets game)
    {
        var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (FilterEntry entry in entries)
        {
            if (!FilterSpec.IsLoadableName(entry.Name))
            {
                throw new InstallException(
                    $"'{entry.Name}' contains a space. A filter name is a localization key, so the " +
                    "game would list it, let it be selected, and never load it — with no error.");
            }

            byte[] bytes = bundle.ReadFilter(entry, game);
            writes[RefPath.Canon(entry.InstallRef)] = bytes;

            foreach (string curve in CurveRefs(bytes))
            {
                if (writes.ContainsKey(curve) || resolved.Contains(curve))
                    continue;

                // The game's own copy wins: a filter referencing stock natural1 curves must use the
                // ones already there rather than have us plant a second copy over them.
                if (game.Has(curve))
                {
                    resolved.Add(curve);
                    continue;
                }

                if (bundle.ReadAsset(curve, game) is { } supplied)
                    writes[curve] = supplied;
                else
                    missing.Add($"{entry.Name} needs {FilterSpec.TablePath(curve)}");
            }
        }

        if (missing.Count > 0)
        {
            throw new InstallException(
                $"{bundle.Describe} is missing {missing.Count} file(s) its filters reference, so " +
                "nothing was installed:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", missing));
        }

        return new FilterPlan(writes, resolved);
    }
}
