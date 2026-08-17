using EvoMods.App.Pages;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    }

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
            _ => typeof(HomePage),
        });
    }
}
