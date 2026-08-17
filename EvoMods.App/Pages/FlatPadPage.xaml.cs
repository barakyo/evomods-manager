using System.Text;

using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EvoMods.App.Pages;

/// <summary>Building, removing and checking the Flat Pad track.</summary>
public sealed partial class FlatPadPage : Page
{
    private readonly StringBuilder _log = new();
    private string? _gameRoot;

    public FlatPadPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    // ---- state

    private void Refresh()
    {
        _gameRoot = AppInfo.GameRoot;
        if (_gameRoot is null)
        {
            Blocked("No install found",
                "Assetto Corsa EVO could not be located. Nothing here can run without it.");
            return;
        }

        GameArchiveState archive = GameArchive.Detect(_gameRoot);
        if (archive.Mode == ArchiveMode.Packed)
        {
            // Tracks are the reason unpacking exists: they are not loaded from Saved Games\ACE\mods\,
            // so while the archive is live a perfectly built track simply never appears.
            Blocked("The game is still packed",
                "Tracks only load from loose folders, so the game has to be unpacked first. "
                + "The Game screen does that — it is a big, slow, one-off operation.");
            return;
        }

        FlatPadState state = Verifier.DetectState(_gameRoot);
        StatusText.Text = state switch
        {
            FlatPadState.Installed => "Installed — it will appear in the track list.",
            FlatPadState.FilesPresentButNotRegistered =>
                "Files are on disk but the registry entries are gone, so it will not appear in any "
                + "track list. Installing again puts them back.",
            _ => "Not installed.",
        };

        // Install stays available in every state, because it is also the REPAIR action: a game update
        // re-packs the game and restores stock content, and re-running install is the documented fix.
        // Disabling it once the track is present would remove the remedy exactly when it is needed.
        InstallButton.Content = state switch
        {
            FlatPadState.Installed => "Reinstall Flat Pad",
            FlatPadState.FilesPresentButNotRegistered => "Repair Flat Pad",
            _ => "Install Flat Pad",
        };

        if (state == FlatPadState.FilesPresentButNotRegistered)
        {
            Warn("Registered entries are missing",
                "Unpacking the game replaces the registries, which is what removes them. "
                + "Press Repair Flat Pad to put them back.",
                InfoBarSeverity.Warning);
        }
        else
        {
            Notice.IsOpen = false;
        }

        bool present = state != FlatPadState.NotInstalled;
        SetEnabled(install: true, uninstall: present, verify: present);
    }

    private void Blocked(string title, string why)
    {
        StatusText.Text = "—";
        InstallButton.Content = "Install Flat Pad";
        Warn(title, why, InfoBarSeverity.Warning);
        SetEnabled(install: false, uninstall: false, verify: false);
    }

    private void Warn(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private void SetEnabled(bool install, bool uninstall, bool verify)
    {
        InstallButton.IsEnabled = install;
        UninstallButton.IsEnabled = uninstall;
        VerifyButton.IsEnabled = verify;
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        LogText.Text = _log.ToString();
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
    }

    // ---- actions

    private async void OnInstall(object sender, RoutedEventArgs e) =>
        await Run("Installing Flat Pad", log => new Installer(_gameRoot!, log).Install());

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (await Confirm("Remove Flat Pad?",
                "Its folder and its registry entries go. Nothing the game shipped is touched, and it "
                + "can be built again at any time.", "Remove"))
        {
            await Run("Removing Flat Pad", log => new Installer(_gameRoot!, log).Uninstall());
        }
    }

    /// <summary>Verify, then offer the one repair that pressing Install again cannot do.</summary>
    /// <remarks>
    /// Everything else verification reports is fixed by reinstalling. Missing BASE-GAME registry
    /// entries are not: rebuilding our own track cannot put back a catalog entry that was deleted
    /// from underneath somebody else's. So it is offered here, where the damage has actually been
    /// demonstrated rather than guessed at — and never as a standing button, because knowing means
    /// opening the game's ~68 GB archive.
    /// </remarks>
    private async void OnVerify(object sender, RoutedEventArgs e)
    {
        VerifyReport? report = null;
        await Run("Verifying", log =>
        {
            report = new Verifier(_gameRoot!).Run();
            log(report.Render().TrimEnd());
        });

        if (report?.Registry is not { Damaged.Count: > 0 } diff)
            return;

        string tracks = string.Join(Environment.NewLine + "  ", diff.DamagedTracks);
        if (await Confirm("Registry entries are missing",
                $"""
                These base-game tracks have files on disk but are missing from the game's track
                registry, so they will not appear in any menu:

                  {tracks}

                Their entries are still in the game's own content archive ({diff.Source}).
                Restore just those entries? Nothing else in the registry is touched.
                """, "Restore"))
        {
            await Run("Repairing the registry", log => new Installer(_gameRoot!, log).RepairRegistry(diff));
        }
    }

    // ---- plumbing

    /// <summary>Run a Core operation off the UI thread, with the buttons locked and the log live.</summary>
    /// <remarks>
    /// Indeterminate on purpose: <see cref="Installer"/> takes a log rather than an
    /// <see cref="IProgress{T}"/>, because the work is a few dozen named steps rather than 119,000
    /// files. The log IS the progress, so a percentage would have to be invented.
    /// <para>
    /// Log lines are marshalled one at a time rather than collected and flushed at the end, because a
    /// log that only appears once the work is over is not progress — it is a receipt. Core writes
    /// from the background thread, so each line goes through the dispatcher. No throttling here,
    /// unlike unpacking: this is dozens of lines, not a hundred thousand.
    /// </para>
    /// </remarks>
    private async Task Run(string title, Action<Action<string>> work)
    {
        if (_gameRoot is null)
            return;

        SetEnabled(install: false, uninstall: false, verify: false);
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = $"{title}…";
        Notice.IsOpen = false;

        void Emit(string line) => DispatcherQueue.TryEnqueue(() => Log(line));

        try
        {
            await Task.Run(() => work(Emit));

            // In the log, not in ProgressText: the progress panel is collapsed a moment later, so
            // anything written there is set and hidden without ever being read.
            Log($"{title} — done.");
        }
        catch (Exception ex)
        {
            Log($"{title} failed: {ex.Message}");
            Warn($"{title} failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    private async Task<bool> Confirm(string title, string body, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
