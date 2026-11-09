using System.Windows;
using System.Windows.Threading;
using DownloadDock.Services;

namespace DownloadDock;

public partial class App
{
    private SingleInstance? _instance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless diagnostics (used by CI and for troubleshooting previews).
        if (Diagnostics.TryRun(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        // Documented command line (used by Finder for Windows and shortcuts):
        //   --background     start (or stay) in the tray without opening anything
        //   --show-fan       open the Downloads fan (the default when started by hand)
        //   --open-settings  open the Settings window
        var background = HasArg(e.Args, "--background");
        var openSettings = HasArg(e.Args, "--open-settings");

        _instance = new SingleInstance("DownloadDock-7b1f0c3e");
        if (!_instance.IsFirstInstance)
        {
            // Already running: forward the request to that instance, then quit.
            if (!background) ForwardToRunningInstance(openSettings);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _controller = new AppController(Dispatcher);
        _controller.Start(openFanNow: !background && !openSettings);
        if (openSettings) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _controller?.OpenSettingsFromExternalLaunch());
        _instance.ListenForSignals(() => Dispatcher.BeginInvoke(() => _controller?.OpenFanFromExternalLaunch()));
    }

    private static bool HasArg(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks the running instance (through its own integration pipe) to open settings or the fan.</summary>
    private void ForwardToRunningInstance(bool openSettings)
    {
        try
        {
            var client = new DownloadDock.Integration.IntegrationClient(new DownloadDock.Integration.IntegrationClientOptions
            {
                PipeName = DownloadDock.Integration.IntegrationEndpoints.DownloadDockPipeName(),
                ClientName = "DownloadDock.Launcher",
                ConnectTimeout = TimeSpan.FromSeconds(1),
                RequestTimeout = TimeSpan.FromSeconds(3),
            });
            Task.Run(async () =>
            {
                await using (client)
                {
                    var hello = await client.ConnectAsync();
                    // Let the running instance bring its window to the front.
                    Interop.ScreenshotNative.AllowSetForegroundWindow(hello.ProcessId);
                    await client.SendAsync<object>(openSettings ? DownloadDock.Integration.IntegrationCommands.OpenSettings : DownloadDock.Integration.IntegrationCommands.ShowFan);
                }
            }).Wait(TimeSpan.FromSeconds(5));
            return;
        }
        catch (Exception ex) when (ex is AggregateException or DownloadDock.Integration.IntegrationException)
        {
            Log.Info("Forwarding over the integration pipe failed; using the show-fan signal instead");
        }
        if (!openSettings) _instance?.SignalFirstInstance();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A tray utility must not vanish because of one bad file: log and keep running.
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }
}
