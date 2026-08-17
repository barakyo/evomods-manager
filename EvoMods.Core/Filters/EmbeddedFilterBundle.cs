using System.Reflection;

using EvoMods.Core.FlatPad;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Filters;

/// <summary>The filters this app carries, installed without downloading or dropping anything.</summary>
/// <remarks>
/// All five together are 5.5 KB, which is why they are embedded rather than fetched: against a
/// ~95 MB installer it is not a size worth a network round trip, and a filter that ships with the
/// app cannot arrive damaged, half-extracted or missing a curve.
/// <para>
/// The trade is that publishing a new filter means publishing a new build — cheap here, because a
/// Velopack delta between two builds of this app measures in hundreds of kilobytes.
/// </para>
/// </remarks>
public sealed class EmbeddedFilterBundle : IFilterBundle
{
    private const string Prefix = "ppfilters/";

    private static readonly Assembly Owner = typeof(EmbeddedFilterBundle).Assembly;

    /// <summary>Resource names by install-relative path, with MSBuild's backslashes normalised.</summary>
    private static readonly Dictionary<string, string> Resources =
        Owner.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    public string Describe => "built in";

    /// <summary>
    /// Registration order. Underscores rather than spaces, because the name is a localization key
    /// and a space is a silent load failure — see <see cref="FilterSpec.IsLoadableName"/>.
    /// </summary>
    public IReadOnlyList<FilterEntry> Filters { get; } =
    [
        new("Video_Hero", "video_hero"),
        new("Video_Hero_Soft", "video_hero_soft"),
        new("Video_Punch", "video_punch"),
        new("Video_Cine", "video_cine"),
        new("Video_Clean", "video_clean"),
    ];

    public byte[] ReadFilter(FilterEntry filter, IGameAssets game) =>
        Bytes(filter.InstallRef)
        ?? throw new InstallException(
            $"'{filter.Name}' is named in this build but its {filter.Folder} resource is not — the " +
            "assembly was built without it.");

    /// <summary>
    /// Anything under the post-processing folder that this build happens to carry.
    /// </summary>
    /// <remarks>
    /// Reached for the curve <c>Video_Hero</c> and <c>Video_Hero_Soft</c> both name, which lives in
    /// <c>pure_gamma_full/</c> — a folder belonging to a filter this bundle does not offer. Because
    /// the planner asks by the reference the filter itself carries, installing either one alone
    /// still plants it.
    /// </remarks>
    public byte[]? ReadAsset(string reference, IGameAssets game) => Bytes(reference);

    private static byte[]? Bytes(string reference)
    {
        string canon = RefPath.Canon(reference);
        string root = FilterSpec.PpDir + "/";
        if (!canon.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Resources.TryGetValue(Prefix + canon[root.Length..], out string? name))
            return null;

        using Stream stream = Owner.GetManifestResourceStream(name)!;
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
