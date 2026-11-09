using System.Windows.Threading;
using DownloadDock.Core.Integration;
using DownloadDock.Integration;

namespace DownloadDock.Integration;

/// <summary>
/// Bridges integration commands (background threads) to the WPF application. Every UI action is
/// marshalled to the dispatcher asynchronously – the pipe threads never block the UI and the UI never
/// waits on a pipe. Status is read from fields updated on the UI thread.
/// </summary>
internal sealed class AppSurface : IDownloadDockSurface
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<string> _showFan;
    private readonly Func<string> _closeFan;
    private readonly Func<string> _openSettings;

    public AppSurface(Dispatcher dispatcher, Func<string> showFan, Func<string> closeFan, Func<string> openSettings)
    {
        _dispatcher = dispatcher;
        _showFan = showFan;
        _closeFan = closeFan;
        _openSettings = openSettings;
    }

    // Updated by AppController on the UI thread; read from pipe threads.
    public volatile string? DownloadsFolder;
    public volatile string? ScreenshotFolder;
    public volatile bool ScreenshotsEnabled;
    public volatile bool FanOpen;
    public string? AppVersion { get; init; }

    public Task<string> ShowFanAsync(CancellationToken ct) => _dispatcher.InvokeAsync(_showFan, DispatcherPriority.Normal, ct).Task;

    public Task<string> CloseFanAsync(CancellationToken ct) => _dispatcher.InvokeAsync(_closeFan, DispatcherPriority.Normal, ct).Task;

    public Task<string> OpenSettingsAsync(CancellationToken ct) => _dispatcher.InvokeAsync(_openSettings, DispatcherPriority.Normal, ct).Task;

    public DownloadDockStatus GetStatus() => new()
    {
        AppVersion = AppVersion,
        DownloadsFolder = DownloadsFolder,
        ScreenshotFolder = ScreenshotsEnabled ? ScreenshotFolder : null,
        ScreenshotsEnabled = ScreenshotsEnabled,
        FanOpen = FanOpen,
    };
}
