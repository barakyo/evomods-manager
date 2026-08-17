using Velopack;
using Velopack.Sources;

namespace EvoMods.App;

/// <summary>
/// Self-update, shared by the window's button and the headless <c>--update</c> path.
/// </summary>
/// <remarks>
/// Updates come from GitHub Releases, which needs no infrastructure at all: <c>vpk upload github</c>
/// attaches the packages and a <c>releases.win.json</c> manifest to a release, and the client does
/// the comparing. The manifest carries a SHA1, SHA256 and byte size per package, and nothing is
/// applied until the download hashes match.
/// <para>
/// ⚠️ That is integrity, not authenticity. The manifest is served from the same place as the
/// packages, so whoever can replace one can replace the other to match — it protects against a
/// truncated download, not against a compromised release. Signing is what would change that, so
/// until then the GitHub account's 2FA is the thing actually holding the update channel shut.
/// </para>
/// </remarks>
internal static class Updates
{
    /// <summary>Exit codes for the headless path, so a script can assert on the outcome.</summary>
    public const int UpToDate = 0;
    public const int Failed = 1;
    public const int NotInstalled = 3;
    public const int Applied = 10;

    /// <summary>Where releases live. Public on purpose — a private repo would mean shipping a token.</summary>
    public const string RepoUrl = "https://github.com/barakyo/evomods-manager";

    /// <summary>
    /// A local folder to update from instead, for testing the mechanism without publishing.
    /// </summary>
    /// <remarks>
    /// This is how the whole install-and-update round trip was proved before there was anywhere to
    /// publish to, and it stays because rehearsing a release against a folder beats rehearsing it
    /// against the thing users are pointed at.
    /// </remarks>
    public static string? LocalFeed => Environment.GetEnvironmentVariable("EVOMODS_UPDATE_FEED");

    public static UpdateManager Manager()
    {
        string? local = LocalFeed;
        return string.IsNullOrWhiteSpace(local)
            ? new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false))
            : new UpdateManager(new SimpleFileSource(new DirectoryInfo(local)));
    }

    /// <summary>Where this build is looking, for the screen to say so.</summary>
    public static string Describe() =>
        LocalFeed is { Length: > 0 } local ? local : RepoUrl;

    /// <summary>Check, download and apply without a UI. Returns one of the exit codes above.</summary>
    public static async Task<int> RunHeadlessAsync()
    {
        try
        {
            UpdateManager manager = Manager();
            if (!manager.IsInstalled)
                return NotInstalled;

            UpdateInfo? update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return UpToDate;

            await manager.DownloadUpdatesAsync(update);

            // Not ApplyUpdatesAndExit: that terminates the process itself, which makes the exit
            // code below unreachable and leaves "applied" indistinguishable from "up to date".
            // Scheduling the swap for after exit keeps the outcome reportable.
            manager.WaitExitThenApplyUpdates(update);
            return Applied;
        }
        catch
        {
            return Failed;
        }
    }
}
