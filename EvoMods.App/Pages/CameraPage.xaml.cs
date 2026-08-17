using EvoMods.Core.Camera;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EvoMods.App.Pages;

/// <summary>
/// Tuning the camera settings the game actually honours.
/// </summary>
/// <remarks>
/// Sliders write nothing until Apply. Every change here goes to a file the game reads once at
/// startup, so there is no live preview to be had — a slider that wrote on every tick would just be
/// a slower way to reach the same file, with more chances to leave it half-set.
/// </remarks>
public sealed partial class CameraPage : Page
{
    private readonly Dictionary<CameraField, Slider> _sliders = [];
    private readonly Dictionary<CameraField, TextBlock> _readouts = [];
    private CameraReading _onDisk = new(new Dictionary<int, float>(), Exists: false);

    public CameraPage()
    {
        InitializeComponent();
        Build();
        Loaded += (_, _) => Refresh();
    }

    // ---- the controls

    private void Build()
    {
        CameraEffect? section = null;
        foreach (CameraField field in CameraSpec.Fields)
        {
            if (field.Effect != section)
            {
                section = field.Effect;
                Rows.Children.Add(Header(field.Effect));
            }

            Rows.Children.Add(Row(field));
        }
    }

    /// <summary>
    /// Group by how much is actually known, because that is the thing a reader most needs.
    /// </summary>
    /// <remarks>
    /// Two of these were measured as dramatic and chase-only, so they can go to any extreme. The
    /// rest change the view you drive from, which is a different kind of decision — and burying that
    /// distinction in per-control small print would be the wrong place for it.
    /// </remarks>
    private static TextBlock Header(CameraEffect effect) => new()
    {
        Text = effect == CameraEffect.Works
            ? "Chase camera only — measured, and dramatic"
            : "Affects the view you drive from",
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 8, 0, 0),
    };

    private UIElement Row(CameraField field)
    {
        var value = new TextBlock
        {
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };

        var slider = new Slider
        {
            Minimum = field.Min,
            Maximum = field.Max,
            StepFrequency = Step(field),
            Width = 320,
            Margin = new Thickness(0, -4, 0, -4),
        };
        slider.ValueChanged += (_, _) =>
        {
            value.Text = slider.Value.ToString("0.##");
            UpdateButtons();
        };

        _sliders[field] = slider;
        _readouts[field] = value;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new TextBlock
        {
            Text = field.Label,
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        head.Children.Add(slider);
        head.Children.Add(value);
        head.Children.Add(new TextBlock
        {
            Text = $"stock {field.Stock:0.##}",
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(head);
        row.Children.Add(new TextBlock
        {
            Text = field.Note,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        return row;
    }

    /// <summary>Fine enough to tune with, coarse enough that the number stops jittering.</summary>
    private static double Step(CameraField field) => (field.Max - field.Min) <= 1.5 ? 0.01 : 0.05;

    // ---- state

    private void Refresh()
    {
        _onDisk = CameraSettings.Read();
        foreach ((CameraField field, Slider slider) in _sliders)
        {
            slider.Value = _onDisk.ValueOf(field);

            // Set the readout here rather than leaning on ValueChanged: assigning a value the slider
            // already holds raises no event, so a setting that happens to sit at the slider's
            // starting value would show a blank number. Horizon lock at 0 does exactly that.
            _readouts[field].Text = slider.Value.ToString("0.##");
        }

        if (!_onDisk.Exists)
        {
            Warn("No settings file yet",
                "Launch the game once so it writes one. This edits the file the game already has "
                + "rather than inventing one.", InfoBarSeverity.Warning);
        }
        else if (CameraSettings.RunningGame() is { } game)
        {
            Warn("The game is running",
                $"Assetto Corsa EVO (PID {game.Id}) reads this file at startup and rewrites it on "
                + "exit, so nothing can be saved while it is up. Close it and come back.",
                InfoBarSeverity.Warning);
        }
        else
        {
            // The one way to lose these values without doing anything obviously wrong.
            Warn("After saving",
                "Do not open the in-game camera settings screen — the game rewrites this file and "
                + "discards these values. Changes take effect the next time the game starts.",
                InfoBarSeverity.Informational);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool dirty = _sliders.Any(s => Math.Abs(s.Value.Value - _onDisk.ValueOf(s.Key)) > 1e-4);
        bool writable = _onDisk.Exists && CameraSettings.RunningGame() is null;

        ApplyButton.IsEnabled = dirty && writable;
        RevertButton.IsEnabled = dirty;
        RestoreButton.IsEnabled = writable;
    }

    private void Warn(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    // ---- actions

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        Refresh();
        StatusText.Text = "Back to what the file says.";
    }

    private async void OnApply(object sender, RoutedEventArgs e) =>
        await Run(log => CameraSettings.Write(
            _sliders.ToDictionary(s => s.Key.Number, s => (float)s.Value.Value), log));

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (await Confirm("Restore the game's defaults?",
                "Every setting here goes back to the value the game ships. A copy of the current "
                + "file is kept alongside it first.", "Restore"))
        {
            await Run(log => CameraSettings.Restore(log));
        }
    }

    private async Task Run(Func<Action<string>, int> work)
    {
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        StatusText.Text = "Saving…";

        var lines = new List<string>();
        try
        {
            int changed = await Task.Run(() => work(lines.Add));
            StatusText.Text = changed == 0
                ? "Nothing to change — the file already says that."
                : $"Saved {changed} setting(s). They apply next time the game starts.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Warn("That didn't work", ex.Message, InfoBarSeverity.Error);
        }

        Refresh();
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
