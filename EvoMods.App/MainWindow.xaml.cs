using System.Reflection;

using FlatPad.Core.Game;

using Microsoft.UI.Xaml;

using Velopack;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace EvoMods.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "EvoMods Manager";

        VersionText.Text = $"Version {CurrentVersion()}";
        GameText.Text = DescribeGame();
    }

    private static string CurrentVersion() =>
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0]
        ?? "unknown";

    /// <summary>Calls into FlatPad.Core, which is the point: the reference has to resolve at run time.</summary>
    private static string DescribeGame()
    {
        try
        {
            List<string> found = GameLocator.FindAll();
            return found.Count switch
            {
                0 => "Not found. You'll be able to browse for it once that screen exists.",
                1 => found[0],
                _ => string.Join(Environment.NewLine, found),
            };
        }
        catch (Exception ex)
        {
            return $"Lookup failed: {ex.Message}";
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Inspect";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            DropText.Text = "That wasn't a file.";
            return;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            DropText.Text = items.Count == 0
                ? "Nothing came through."
                : string.Join(Environment.NewLine, items.Select(i => i.Name));
        }
        catch (Exception ex)
        {
            DropText.Text = $"Drop failed: {ex.Message}";
        }
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
                UpdateText.Text = $"Up to date at {CurrentVersion()}.";
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
