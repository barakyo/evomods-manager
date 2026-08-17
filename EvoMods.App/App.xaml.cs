using Microsoft.UI.Xaml;

namespace EvoMods.App;

public partial class App : Application
{
    /// <summary>
    /// The one window, for anything that needs its handle.
    /// </summary>
    /// <remarks>
    /// Unpackaged apps have to hand a real HWND to the file pickers — they cannot infer one the way
    /// a packaged app can — so a page opening a picker needs a way back to the window.
    /// </remarks>
    public static Window? Window { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }
}
