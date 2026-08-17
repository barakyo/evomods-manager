using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using Velopack;

namespace EvoMods.App;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // First statement, before any WinUI initialisation. Velopack routes its install, update
        // and uninstall hooks through this same executable, and those runs have to complete and
        // exit before a window is ever created.
        VelopackApp.Build().Run();

        // Headless update, so the release pipeline can be verified without a human clicking a
        // button — and the hook an "update on launch" flow will use later.
        if (args.Contains("--update", StringComparer.OrdinalIgnoreCase))
        {
            // Environment.Exit rather than a return: WaitExitThenApplyUpdates leaves Update.exe
            // blocked until this process actually terminates, and returning from Main is not
            // enough to do that — a lingering foreground thread keeps it alive and the update
            // never gets applied.
            Environment.Exit(Updates.RunHeadlessAsync().GetAwaiter().GetResult());
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
