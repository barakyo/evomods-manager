using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Velopack;

namespace EvoMods.App.Pages;

/// <summary>Where the game is, which build this is, and whether a newer one exists.</summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();

        VersionText.Text = $"Version {AppInfo.Version}";
        GameText.Text = DescribeGame();
    }

    private static string DescribeGame()
    {
        List<string> found = AppInfo.FindGames(out string? error);
        if (error is not null)
            return $"Lookup failed: {error}";

        return found.Count switch
        {
            0 => "Not found. You'll be able to browse for it once that screen exists.",
            1 => found[0],
            _ => string.Join(Environment.NewLine, found),
        };
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        UpdateManager? manager = Updates.Manager();
        if (manager is null)
        {
            UpdateText.Text = "No update feed configured. Set EVOMODS_UPDATE_FEED.";
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            UpdateText.Text = "Checking…";

            if (!manager.IsInstalled)
            {
                UpdateText.Text = "Running from the build output, so there is nothing to update.";
                return;
            }

            UpdateInfo? update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                UpdateText.Text = $"Up to date at {AppInfo.Version}.";
                return;
            }

            UpdateText.Text = $"Downloading {update.TargetFullRelease.Version}…";
            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            UpdateText.Text = $"Update failed: {ex.Message}";
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }
}
