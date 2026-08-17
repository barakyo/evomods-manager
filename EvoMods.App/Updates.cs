using Velopack;
using Velopack.Sources;

namespace EvoMods.App;

/// <summary>
/// Self-update, shared by the window's button and the headless <c>--update</c> path.
/// </summary>
/// <remarks>
/// The feed is a local folder for now. That is enough to prove the whole install-and-update round
/// trip without standing up hosting first, and swapping in <c>GithubSource</c> later touches only
/// <see cref="Manager"/>.
/// </remarks>
internal static class Updates
{
    /// <summary>Exit codes for the headless path, so a script can assert on the outcome.</summary>
    public const int UpToDate = 0;
    public const int Failed = 1;
    public const int NoFeed = 2;
    public const int NotInstalled = 3;
    public const int Applied = 10;

    public static string? Feed => Environment.GetEnvironmentVariable("EVOMODS_UPDATE_FEED");

    public static UpdateManager? Manager()
    {
        string? feed = Feed;
        return string.IsNullOrWhiteSpace(feed)
            ? null
            : new UpdateManager(new SimpleFileSource(new DirectoryInfo(feed)));
    }

    /// <summary>Check, download and apply without a UI. Returns one of the exit codes above.</summary>
    public static async Task<int> RunHeadlessAsync()
    {
        try
        {
            UpdateManager? manager = Manager();
            if (manager is null)
                return NoFeed;
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
