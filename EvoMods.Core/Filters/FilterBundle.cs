namespace EvoMods.Core.Filters;

/// <summary>A filter on offer, before anything has been read.</summary>
/// <param name="Name">
/// The registry name, which is a localization key — so no spaces. See
/// <see cref="FilterSpec.IsLoadableName"/> for what a space actually costs.
/// </param>
/// <param name="Folder">Install folder under <c>content/tracks/common_assets/post_process</c>.</param>
public sealed record FilterEntry(string Name, string Folder)
{
    /// <summary>Canonical reference of the filter's own asset.</summary>
    public string InstallRef => FilterSpec.InstallRef(Folder);
}

/// <summary>A source of filters. The install path knows nothing about where they came from.</summary>
/// <remarks>
/// The seam is at "a bundle" rather than "a file on disk" so that a dropped zip, a fetched pack and
/// the filters carried in this assembly are three implementations of one install, one validation and
/// one uninstall. Two rules are what make that hold:
/// <list type="number">
/// <item>
/// <see cref="ReadAsset"/> is keyed by GAME-RELATIVE REFERENCE, not by a bundle-internal path. The
/// installer works out what it needs by walking each filter's own curve references and asks for them
/// by the path the filter itself names, so a bundle never declares its extras and therefore cannot
/// get that declaration wrong. The reference implementation declares them by hand and does get it
/// wrong: it hangs the shared exposure curve off <c>video_hero</c> and gives <c>video_hero_soft</c>
/// an empty list though both reference it, so installing Soft without Hero ships a filter pointing
/// at a curve that is not there.
/// </item>
/// <item>
/// <see cref="ReadFilter"/> is handed the game's own assets, so a bundle may DERIVE a filter from
/// the user's install rather than carry bytes — which is what lets one install path serve a
/// recipe-based bundle that redistributes nothing.
/// </item>
/// </list>
/// </remarks>
public interface IFilterBundle
{
    /// <summary>Where these came from, for the log and the report.</summary>
    string Describe { get; }

    /// <summary>The filters on offer, in the order they should be registered.</summary>
    IReadOnlyList<FilterEntry> Filters { get; }

    /// <summary>The <c>.postprocessing</c> bytes for one of <see cref="Filters"/>.</summary>
    byte[] ReadFilter(FilterEntry filter, IGameAssets game);

    /// <summary>Bytes this bundle can supply at a game-relative reference, or null if it cannot.</summary>
    byte[]? ReadAsset(string reference, IGameAssets game);
}
