using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Screenshots;
using DownloadDock.Interop;
using DownloadDock.Services;

namespace DownloadDock.Screenshots;

/// <summary>
/// Shows DownloadDock's thumbnail for screenshots taken with Windows' own tools, using only supported,
/// read-only signals – it never changes the clipboard or intercepts Windows' shortcuts:
///  1. New image files in the Screenshots folder (Snipping Tool auto-save – on by default in Windows 11 –
///     and Win+PrtScn).
///  2. Clipboard updates (AddClipboardFormatListener) carrying an image whose owner is a Windows capture
///     tool (Win+Shift+S / Snipping Tool), or arriving just after Print Screen was pressed.
/// Clipboard-only captures are saved per the screenshot storage setting so they can be dragged as files.
/// Limitations are documented in the README (e.g. third-party tools, or Print Screen with "open Snipping
/// Tool" turned off and an app that re-copies images).
/// </summary>
internal sealed class WindowsScreenshotDetector : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ScreenshotStore _store;
    private readonly ScreenshotCorrelator _correlator = new();
    private readonly HashSet<string> _seenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly CompletionChecker _completion = new();
    private HwndSource? _listener;
    private FileSystemWatcher? _watcher;
    private BitmapSource? _pendingClipboardImage;
    private DispatcherTimer? _pendingTimer;

    public event Action<ScreenshotItem>? ScreenshotDetected;

    public bool Enabled { get; set; } = true;

    public string? WatchedFolder => _watcher?.Path;

    public WindowsScreenshotDetector(Dispatcher dispatcher, ScreenshotStore store)
    {
        _dispatcher = dispatcher;
        _store = store;
    }

    public void Start(string screenshotsFolder)
    {
        StartClipboardListener();
        WatchFolder(screenshotsFolder);
    }

    /// <summary>Called when the keyboard hook sees Print Screen (the key itself is never consumed).</summary>
    public void OnPrintScreen() => _correlator.PrintScreenPressed(DateTime.UtcNow);

    // ------------------------------------------------------------------ clipboard

    private void StartClipboardListener()
    {
        if (_listener is not null) return;
        var p = new HwndSourceParameters("DownloadDockClipboardListener")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: a message-only window
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _listener = new HwndSource(p);
        _listener.AddHook(ClipboardHook);
        if (!ScreenshotNative.AddClipboardFormatListener(_listener.Handle))
            Log.Error("Clipboard listener could not be registered");
    }

    private IntPtr ClipboardHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ScreenshotNative.WM_CLIPBOARDUPDATE && Enabled)
        {
            var owner = ClipboardOwnerProcess();
            var now = DateTime.UtcNow;
            // Decide synchronously (cheap) and read the clipboard after the owner has finished writing.
            if (_correlator.IsScreenshotClipboard(owner, now))
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () => ReadClipboardScreenshot(owner));
        }
        return IntPtr.Zero;
    }

    internal static string? ClipboardOwnerProcess()
    {
        try
        {
            var owner = ScreenshotNative.GetClipboardOwner();
            if (owner == IntPtr.Zero) return null;
            ScreenshotNative.GetWindowThreadProcessId(owner, out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void ReadClipboardScreenshot(string? owner)
    {
        var image = TryReadClipboardImage();
        if (image is null) return;
        Log.Info($"Clipboard screenshot detected (owner: {owner ?? "none"}, {image.PixelWidth}×{image.PixelHeight})");
        _correlator.ClipboardScreenshotArrived(DateTime.UtcNow);
        _pendingClipboardImage = image;
        _pendingTimer?.Stop();
        _pendingTimer = new DispatcherTimer(_correlator.FileWait + TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _pendingTimer?.Stop();
            if (_pendingClipboardImage is { } pending && _correlator.ResolvePendingClipboard(DateTime.UtcNow))
            {
                // No auto-saved file turned up: keep the clipboard capture as a file of our own.
                _pendingClipboardImage = null;
                ScreenshotDetected?.Invoke(_store.Save(pending, "clipboard", ScreenCapture.MonitorAtCursor().Handle));
            }
        }, _dispatcher);
        _pendingTimer.Start();
    }

    /// <summary>Reads (never modifies) an image from the clipboard; prefers lossless PNG data when present.</summary>
    internal static BitmapSource? TryReadClipboardImage()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png)
                {
                    var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    frame.Freeze();
                    return frame;
                }
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
                {
                    // CF_DIB alpha is unreliable: treat as opaque.
                    var opaque = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
                    var copy = new WriteableBitmap(opaque);
                    copy.Freeze();
                    return copy;
                }
                return null;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException or NotSupportedException or FileFormatException)
            {
                Thread.Sleep(60); // another process has the clipboard open
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ Screenshots folder

    private void WatchFolder(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            Directory.CreateDirectory(folder);
            var w = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += (_, e) => _dispatcher.BeginInvoke(() => Consider(e.FullPath));
            w.Renamed += (_, e) => _dispatcher.BeginInvoke(() => Consider(e.FullPath));
            w.Error += (_, e) => Log.Error("Screenshots folder watcher error", e.GetException());
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Error($"Cannot watch {folder}", ex);
        }
    }

    private void Consider(string path, int attempt = 0)
    {
        if (!Enabled || !ScreenshotNaming.IsImageFile(path) || _store.IsOwnFile(path) || _seenFiles.Contains(path)) return;
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        // Ignore old files that merely appear (folder moved/synced); screenshots are brand new.
        if (DateTime.UtcNow - info.CreationTimeUtc > TimeSpan.FromMinutes(1) && DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromMinutes(1)) return;

        var entry = new DownloadEntry(path, info.Name, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        if (_completion.Check(entry) == CompletionChecker.State.Pending || info.Length == 0)
        {
            if (attempt < 20)
            {
                var retry = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (s, _) =>
                {
                    ((DispatcherTimer)s!).Stop();
                    Consider(path, attempt + 1);
                }, _dispatcher);
                retry.Start();
            }
            return;
        }

        _seenFiles.Add(path);
        if (!_correlator.FileScreenshotArrived(DateTime.UtcNow)) return;
        _pendingClipboardImage = null;

        BitmapSource image;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            image = bi;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            Log.Error($"Could not read new screenshot {path}", ex);
            return;
        }
        Log.Info($"Windows screenshot file detected: {path}");
        ScreenshotDetected?.Invoke(ScreenshotStore.FromExistingFile(path, image, "windows-file", ScreenCapture.MonitorAtCursor().Handle));
    }

    public void Dispose()
    {
        _pendingTimer?.Stop();
        _watcher?.Dispose();
        if (_listener is not null)
        {
            ScreenshotNative.RemoveClipboardFormatListener(_listener.Handle);
            _listener.Dispose();
        }
    }
}
