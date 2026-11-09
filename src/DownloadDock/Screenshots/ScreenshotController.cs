using System.Windows;
using System.Windows.Threading;
using DownloadDock.Core.Screenshots;
using DownloadDock.Core.Settings;
using DownloadDock.Services;

namespace DownloadDock.Screenshots;

/// <summary>
/// Owns the screenshot feature: global shortcuts, full-screen and region capture, storage, the floating
/// thumbnails, Windows-screenshot detection and the editor. Independent of the Downloads fan.
/// </summary>
internal sealed class ScreenshotController : IDisposable
{
    public const string FullScreenId = "fullscreen";
    public const string RegionId = "region";

    private readonly Dispatcher _dispatcher;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _janitor;
    private bool _capturing;

    public HotkeyService Hotkeys { get; }
    public ScreenshotStore Store { get; }
    public ScreenshotToastManager Toasts { get; }
    public RegionSelector Region { get; }
    public WindowsScreenshotDetector Detector { get; }

    /// <summary>Shortcut registration problems, for the Settings window (id → message).</summary>
    public IReadOnlyDictionary<string, string> ShortcutProblems { get; private set; } = new Dictionary<string, string>();

    public ScreenshotController(Dispatcher dispatcher, Func<AppSettings> settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        Store = new ScreenshotStore(settings);
        Hotkeys = new HotkeyService(dispatcher);
        Toasts = new ScreenshotToastManager(settings, Store, dispatcher);
        Region = new RegionSelector(Hotkeys);
        Detector = new WindowsScreenshotDetector(dispatcher, Store);

        Hotkeys.HotkeyPressed += id =>
        {
            if (id == FullScreenId) _ = CaptureFullScreenAsync();
            else if (id == RegionId) _ = CaptureRegionAsync();
        };
        Hotkeys.PrintScreenObserved += Detector.OnPrintScreen;
        Detector.ScreenshotDetected += item => Toasts.Show(item);
        Toasts.OpenRequested += OpenEditor;

        _janitor = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => Store.CleanTemporaryFiles(), dispatcher);
    }

    public void Start()
    {
        ApplySettings();
        Detector.Start(ScreenshotStore.WindowsScreenshotsFolder());
        Store.CleanTemporaryFiles();
        _janitor.Start();
    }

    public void ApplySettings()
    {
        var s = _settings();
        var problems = new Dictionary<string, string>();
        var map = new Dictionary<string, Hotkey>();
        if (s.ScreenshotsEnabled)
        {
            foreach (var (id, text) in new[] { (FullScreenId, s.FullScreenShortcut), (RegionId, s.RegionShortcut) })
            {
                if (string.IsNullOrEmpty(text)) continue;
                if (!Hotkey.TryParse(text, out var hk)) { problems[id] = $"'{text}' is not a valid shortcut."; continue; }
                if (hk.Validate() is { } why) { problems[id] = why; continue; }
                map[id] = hk;
            }
        }
        if (!Hotkeys.IsHooked) problems["hook"] = "Windows refused the keyboard hook, so shortcuts are unavailable (use the tray menu).";
        Hotkeys.SetHotkeys(map);
        ShortcutProblems = problems;
        Detector.Enabled = s.ScreenshotsEnabled && s.DetectWindowsScreenshots;
        foreach (var p in problems) Log.Error($"Shortcut {p.Key}: {p.Value}");
    }

    /// <summary>Captures the monitor under the mouse (macOS ⌘⇧3).</summary>
    public async Task<ScreenshotItem?> CaptureFullScreenAsync()
    {
        if (_capturing || Region.IsActive) return null;
        _capturing = true;
        try
        {
            // Let the key release / menu close repaint before grabbing pixels.
            await Task.Delay(120);
            var monitor = ScreenCapture.MonitorAtCursor();
            var image = await Task.Run(() => ScreenCapture.Capture(monitor.Bounds));
            var item = Store.Save(image, "fullscreen", monitor.Handle);
            Toasts.Show(item);
            Log.Info($"Full-screen capture {image.PixelWidth}×{image.PixelHeight}");
            return item;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or System.IO.IOException)
        {
            Log.Error("Full-screen capture failed", ex);
            return null;
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>Lets the user drag a rectangle (macOS ⌘⇧4).</summary>
    public async Task<ScreenshotItem?> CaptureRegionAsync()
    {
        if (_capturing || Region.IsActive) return null;
        _capturing = true;
        try
        {
            await Task.Delay(80);
            var result = await Region.SelectAsync();
            if (result is null) return null;
            var item = Store.Save(result.Image, "region", result.Monitor);
            Toasts.Show(item);
            Log.Info($"Region capture {result.Image.PixelWidth}×{result.Image.PixelHeight}");
            return item;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or System.IO.IOException)
        {
            Log.Error("Region capture failed", ex);
            Region.Cancel();
            return null;
        }
        finally
        {
            _capturing = false;
        }
    }

    public void OpenEditor(ScreenshotItem item)
    {
        var editor = new ScreenshotEditorWindow(item, Store, _settings().Theme);
        editor.Show();
        editor.Activate();
    }

    public void Dispose()
    {
        _janitor.Stop();
        Region.Cancel();
        Hotkeys.Dispose();
        Detector.Dispose();
        Toasts.Dispose();
    }
}
