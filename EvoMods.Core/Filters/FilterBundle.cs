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
