using EvoMods.App.Pages;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.UI;

namespace EvoMods.App;

/// <summary>
/// The shell. Holds navigation and nothing else — every screen is a page.
/// </summary>
/// <remarks>
/// Deliberately introduced at the second screen rather than the first: a shell designed against one
/// page bakes in that page's shape, and these two are different in kind (a status readout and a list
/// that acts on the game).
/// </remarks>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "EvoMods Manager";
        BrandTitleBar();
    }

    /// <summary>Paint the caption bar in the brand's own dark, rather than the system's.</summary>
    /// <remarks>
    /// The title bar is drawn by Windows and follows the OS light/dark setting, so on a machine set
    /// to light it stays light no matter how dark the app is — a white strip above a near-black
    /// window. An app has to opt out of that explicitly; nothing about RequestedTheme reaches it.
    /// <para>
    /// Guarded because customisation is not available everywhere. Where it is not, the default bar is
    /// mismatched but perfectly usable, which is a better outcome than a crash on startup.
    /// </para>
    /// </remarks>
    private void BrandTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.BackgroundColor = Hex(0x0D, 0x0F, 0x15);
        bar.ForegroundColor = Hex(0xEB, 0xEF, 0xF4);
        bar.InactiveBackgroundColor = Hex(0x0D, 0x0F, 0x15);
        bar.InactiveForegroundColor = Hex(0x6C, 0x76, 0x86);

        // Transparent so the buttons sit on the bar's own colour rather than a second one.
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Hex(0xEB, 0xEF, 0xF4);
        bar.ButtonInactiveForegroundColor = Hex(0x6C, 0x76, 0x86);
        bar.ButtonHoverBackgroundColor = Hex(0x20, 0x24, 0x2C);
        bar.ButtonHoverForegroundColor = Hex(0xEB, 0xEF, 0xF4);
        bar.ButtonPressedBackgroundColor = Hex(0x26, 0x29, 0x2F);
        bar.ButtonPressedForegroundColor = Hex(0xEB, 0xEF, 0xF4);
    }

    private static Color Hex(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private void OnNavLoaded(object sender, RoutedEventArgs e) =>
        Nav.SelectedItem = Nav.MenuItems[0];

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not NavigationViewItem item)
            return;

        ContentFrame.Navigate(item.Tag switch
        {
            "game" => typeof(GamePage),
            "flatpad" => typeof(FlatPadPage),
            "filters" => typeof(FiltersPage),
            "camera" => typeof(CameraPage),
            _ => typeof(HomePage),
        });
    }
}
