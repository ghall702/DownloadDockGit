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

        _instance = new SingleInstance("DownloadDock-7b1f0c3e");
        if (!_instance.IsFirstInstance)
        {
            // Already running: ask that instance to open its fan, then quit.
            _instance.SignalFirstInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _controller = new AppController(Dispatcher);
        _controller.Start(openFanNow: !e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase));
        _instance.ListenForSignals(() => Dispatcher.BeginInvoke(() => _controller?.OpenFanFromExternalLaunch()));
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
