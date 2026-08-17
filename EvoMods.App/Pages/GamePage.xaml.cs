using System.Text;

using EvoMods.Core.Game;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace EvoMods.App.Pages;

/// <summary>
/// Switching the game between packed and unpacked, and unpacking a standalone package.
/// </summary>
/// <remarks>
/// The reason this screen exists before any other feature is worth having: tracks are not loaded
/// from <c>Saved Games\ACE\mods\</c>, so while the game reads its archive, loose content is ignored
/// and nothing else in this app does anything at all.
/// </remarks>
public sealed partial class GamePage : Page
{
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cancellation;
    private string? _gameRoot;

    public GamePage()
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
            PathText.Text = "";
            ArchiveText.Text = "—";
            DiskText.Text = "—";
            Warn("No install found", "Assetto Corsa EVO could not be located. Nothing here can run "
                + "without it.", InfoBarSeverity.Warning);
            SetEnabled(unpack: false, revert: false);
            return;
        }

        PathText.Text = _gameRoot;
        GameArchiveState archive = GameArchive.Detect(_gameRoot);

        ArchiveText.Text = archive.Mode switch
        {
            ArchiveMode.Packed =>
                $"Packed — {Path.GetFileName(archive.LivePackage)}, {GameArchive.Bytes(archive.ArchiveBytes)}. "
                + "The game reads the archive, so loose content and mods are ignored.",
            ArchiveMode.Unpacked => "Unpacked — the game reads loose content, so mods load.",
            _ => "Unrecognised — this does not look like a game folder.",
        };

        string disk = $"{GameArchive.Bytes(archive.FreeBytes)} free";
        if (archive.Mode == ArchiveMode.Packed)
        {
            disk += $" — unpacking needs about {GameArchive.Bytes(archive.RequiredBytes)}. "
                + "The archive is kept, so budget roughly twice its size.";
        }

        DiskText.Text = disk;

        if (archive.Mode == ArchiveMode.Packed && !archive.HasEnoughSpace)
        {
            Warn("Not enough space", $"Unpacking needs about {GameArchive.Bytes(archive.RequiredBytes)} "
                + $"and only {GameArchive.Bytes(archive.FreeBytes)} is free.", InfoBarSeverity.Error);
        }
        else if (archive.DisabledPackages.Count > 1)
        {
            // A game update downloads a fresh archive, so an install accumulates them. Restoring the
            // wrong one puts stale content under a newer build.
            Warn("More than one archive set aside",
                $"{archive.DisabledPackages.Count} renamed archives are here. Reverting will ask which.",
                InfoBarSeverity.Informational);
        }
        else if (archive.Mode == ArchiveMode.Unpacked)
        {
            // The single most expensive mistake available from here, and nothing else warns about it.
            Warn("While unpacked",
                "Do not use Steam's \"Verify integrity of game files\" — it re-downloads the whole "
                + "archive. A game update also puts the game back to packed; unpacking again fixes that.",
                InfoBarSeverity.Informational);
        }
        else
        {
            Notice.IsOpen = false;
        }

        SetEnabled(
            unpack: archive.Mode == ArchiveMode.Packed,
            revert: archive.Mode == ArchiveMode.Unpacked && archive.DisabledPackages.Count > 0);
    }

    private void Warn(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private void SetEnabled(bool unpack, bool revert)
    {
        UnpackButton.IsEnabled = unpack;
        RevertButton.IsEnabled = revert;
        PackageButton.IsEnabled = true;
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        LogText.Text = _log.ToString();
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
    }

    // ---- unpacking the game

    private async void OnUnpack(object sender, RoutedEventArgs e)
    {
        if (_gameRoot is not { } root)
            return;

        GameArchiveState archive = GameArchive.Detect(root);
        bool go = await Confirm("Unpack the game's content archive?",
            $"""
            This writes about {GameArchive.Bytes(archive.RequiredBytes)} to disk and takes a while.

            The archive itself is kept, renamed to content.kspkg.bak, so this is reversible.

            Nothing is renamed until every file is out, so cancelling leaves the game playable.
            """,
            "Unpack");

        if (!go)
            return;

        Begin("Unpacking…");
        var progress = new Progress<UnpackProgress>(p =>
        {
            Bar.Value = Math.Clamp(p.Fraction * 100, 0, 100);
            ProgressText.Text = $"Unpacking… {p.FilesDone:N0} / {p.FilesTotal:N0} files";
        });

        try
        {
            CancellationToken token = _cancellation!.Token;
            int files = await Task.Run(() => GameArchive.Unpack(root, progress, token), token);
            Log($"Unpacked {files:N0} files. The game now reads loose content.");
        }
        catch (OperationCanceledException)
        {
            Log("Unpack cancelled — nothing was renamed, so the game still runs. Re-run to continue.");
        }
        catch (Exception ex)
        {
            Failed("Unpack", ex);
        }
        finally
        {
            End();
        }
    }

    private async void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_gameRoot is not { } root)
            return;

        GameArchiveState archive = GameArchive.Detect(root);
        string? chosen = archive.DisabledPackages.Count == 1
            ? archive.DisabledPackages[0]
            : await AskWhichArchive(archive.DisabledPackages);

        if (chosen is null)
            return;

        try
        {
            GameArchive.RevertToPacked(root, chosen);
            Log($"Restored {Path.GetFileName(chosen)} — the game reads packed content again.");
            Log("  The unpacked files are still on disk and are harmless, but the content folder "
                + "can be deleted to reclaim the space.");
        }
        catch (Exception ex)
        {
            Failed("Revert", ex);
        }

        Refresh();
    }

    /// <summary>
    /// Which renamed-aside archive to restore, when there is more than one.
    /// </summary>
    /// <remarks>
    /// Refuse to guess, but offer to ask: a game update leaves archives from different builds behind,
    /// and restoring the wrong one puts stale content under a newer build. Greying the button out
    /// instead would mean "go rename a 68 GB file in Explorer", which is worse and more error-prone
    /// than choosing from a list showing sizes and dates.
    /// </remarks>
    private async Task<string?> AskWhichArchive(IReadOnlyList<string> archives)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = archives.Select(a =>
            {
                var f = new FileInfo(a);
                return $"{f.Name}  —  {GameArchive.Bytes(f.Length)},  {f.LastWriteTime:yyyy-MM-dd HH:mm}";
            }).ToList(),
        };
        list.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Which archive should be restored?",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "A game update downloads a fresh archive, so an install can end up "
                            + "with several. Restoring one from an older build puts stale content "
                            + "under a newer game.",
                    },
                    list,
                },
            },
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0
            ? archives[list.SelectedIndex]
            : null;
    }

    // ---- unpacking a standalone package

    private async void OnUnpackPackage(object sender, RoutedEventArgs e)
    {
        string? path;
        try
        {
            path = await PickPackage();
        }
        catch (Exception ex)
        {
            // An async void handler that throws takes the whole app down, and a picker has plenty of
            // ways to fail on an unpackaged app. Report it on the page instead.
            Failed("Choose a package", ex);
            return;
        }

        if (path is not null)
            await UnpackPackage(path);
    }

    /// <summary>Ask for a <c>.kspkg</c>, or null if the user backed out.</summary>
    /// <remarks>
    /// ⚠️ This is the Windows App SDK picker, NOT <c>Windows.Storage.Pickers.FileOpenPicker</c>. The
    /// UWP one hangs here: called from an unpackaged app it never returns and never throws, and no
    /// dialog window is created anywhere on the desktop — so the button simply does nothing and there
    /// is not even an exception to report. Calling <c>InitializeWithWindow</c> on it, which is the
    /// usual fix, makes no difference. The SDK picker takes a window id instead of needing to be
    /// initialised, and is the one meant for desktop apps.
    /// </remarks>
    private static async Task<string?> PickPackage()
    {
        if (App.Window is not { } window)
            throw new InvalidOperationException("no window to parent the file picker to");

        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id);
        picker.FileTypeFilter.Add(".kspkg");

        Microsoft.Windows.Storage.Pickers.PickFileResult? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// Point at a package anywhere on disk and write its contents somewhere else.
    /// </summary>
    /// <remarks>
    /// Nothing about the game changes — this is not the packed/unpacked switch. It is what someone
    /// taking a car mod apart actually wants.
    /// </remarks>
    private async Task UnpackPackage(string package)
    {
        PackageInfo info;
        Begin("Reading package…");
        Bar.IsIndeterminate = true;
        try
        {
            info = await Task.Run(() => PackageUnpacker.Inspect(package));
        }
        catch (Exception ex)
        {
            Failed("Read package", ex);
            End();
            return;
        }
        finally
        {
            Bar.IsIndeterminate = false;
        }

        End();

        string destination = Path.Combine(
            Path.GetDirectoryName(package)!, Path.GetFileNameWithoutExtension(package) + "_unpacked");

        string what = string.IsNullOrEmpty(info.CommonRoot) ? "" : $"Everything sits under {info.CommonRoot}.\n\n";
        bool go = await Confirm("Unpack this package?",
            $"""
            {Path.GetFileName(package)} holds {info.Files.Count:N0} file(s), {GameArchive.Bytes(info.TotalBytes)}.

            {what}They will be written to:
            {destination}

            Nothing about the game changes.
            """,
            "Unpack");

        if (!go)
            return;

        Begin("Unpacking…");
        var progress = new Progress<UnpackProgress>(p =>
        {
            Bar.Value = Math.Clamp(p.Fraction * 100, 0, 100);
            ProgressText.Text = $"Unpacking… {p.FilesDone:N0} / {p.FilesTotal:N0} files";
        });

        try
        {
            CancellationToken token = _cancellation!.Token;
            UnpackedPackage result = await Task.Run(
                () => PackageUnpacker.Unpack(info, destination, progress, token), token);

            Log($"Wrote {result.Written:N0} file(s), {GameArchive.Bytes(result.BytesWritten)} to {destination}");
            foreach (string refused in result.Refused.Take(5))
                Log($"  refused: {refused}");
            if (result.Refused.Count > 5)
                Log($"  … and {result.Refused.Count - 5:N0} more refused");
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled. Files already written are left where they are.");
        }
        catch (Exception ex)
        {
            Failed("Unpack package", ex);
        }
        finally
        {
            End();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Unpack";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        if (items.FirstOrDefault() is not StorageFile file)
            return;

        if (!file.Path.EndsWith(".kspkg", StringComparison.OrdinalIgnoreCase))
        {
            Log($"{file.Name} is not a .kspkg.");
            return;
        }

        await UnpackPackage(file.Path);
    }

    // ---- plumbing

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancelling…";
        _cancellation?.Cancel();
    }

    private void Begin(string what)
    {
        _cancellation = new CancellationTokenSource();
        ProgressPanel.Visibility = Visibility.Visible;
        Bar.Value = 0;
        ProgressText.Text = what;
        CancelButton.IsEnabled = true;
        SetEnabled(unpack: false, revert: false);
        PackageButton.IsEnabled = false;
    }

    private void End()
    {
        _cancellation?.Dispose();
        _cancellation = null;
        ProgressPanel.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void Failed(string what, Exception e)
    {
        Log($"{what} failed: {e.Message}");
        Warn($"{what} failed", e.Message, InfoBarSeverity.Error);
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
