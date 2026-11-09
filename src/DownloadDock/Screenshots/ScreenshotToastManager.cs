using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DownloadDock.Core.Fan;
using DownloadDock.Core.Screenshots;
using DownloadDock.Core.Settings;
using DownloadDock.Dragging;
using DownloadDock.Interop;
using DownloadDock.Services;
using DownloadDock.Tray;

namespace DownloadDock.Screenshots;

/// <summary>
/// Shows, stacks, times and dismisses the floating screenshot thumbnails. Newest is bottom-right; older
/// ones move up (per monitor). The countdown pauses while hovered or dragged, and a thumbnail that is
/// being dragged is never closed or moved – new screenshots stack around it.
/// </summary>
internal sealed class ScreenshotToastManager : IDisposable
{
    private const int MaxVisible = 4;
    private const int MenuCopy = 1, MenuSave = 2, MenuSaveAs = 3, MenuOpen = 4, MenuEdit = 5, MenuShow = 6, MenuDismiss = 7;

    private readonly Func<AppSettings> _settings;
    private readonly ScreenshotStore _store;
    private readonly List<Entry> _entries = new(); // newest first
    private readonly DispatcherTimer _tick;
    private readonly Dispatcher _dispatcher;

    private sealed class Entry(ScreenshotToastWindow window)
    {
        public ScreenshotToastWindow Window { get; } = window;
        public DateTime Deadline { get; set; }
        public TimeSpan? PausedRemaining { get; set; }
        public bool Hovered { get; set; }
        public bool Closing { get; set; }
    }

    /// <summary>The user asked to open a screenshot (click or menu) – the controller opens the editor.</summary>
    public event Action<ScreenshotItem>? OpenRequested;

    /// <summary>Raised when a thumbnail is shown (tests and diagnostics).</summary>
    public event Action<ScreenshotToastWindow>? ToastShown;

    /// <summary>Raised when a thumbnail has closed.</summary>
    public event Action<ScreenshotToastWindow>? ToastClosed;

    public ScreenshotToastManager(Func<AppSettings> settings, ScreenshotStore store, Dispatcher dispatcher)
    {
        _settings = settings;
        _store = store;
        _dispatcher = dispatcher;
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    public IReadOnlyList<ScreenshotToastWindow> Visible => _entries.Where(e => !e.Closing).Select(e => e.Window).ToList();

    private double AnimationFactor => AppSettings.AnimationFactor(_settings().AnimationSpeed);

    private TimeSpan Duration => TimeSpan.FromSeconds(_settings().ScreenshotThumbnailSeconds);

    public ScreenshotToastWindow Show(ScreenshotItem item)
    {
        var monitor = item.Monitor != IntPtr.Zero ? ScreenCapture.Describe(item.Monitor) : ScreenCapture.MonitorAtCursor();
        var dark = ThemeService.IsDark(_settings().Theme);
        var window = new ScreenshotToastWindow(item, monitor, dark);
        var entry = new Entry(window) { Deadline = DateTime.UtcNow + Duration };
        Wire(entry);
        _entries.Insert(0, entry);

        // Keep the stack manageable: dismiss the oldest (never one that is being dragged).
        foreach (var old in _entries.Where(e => !e.Closing && !e.Window.IsDragging && e.Window.Monitor.Handle == monitor.Handle).Skip(MaxVisible).ToList())
            Dismiss(old, fade: false);

        var positions = Layout(monitor.Handle);
        if (positions.TryGetValue(window, out var rect))
            window.ShowAt(rect, AnimationFactor);
        Restack(monitor.Handle, except: window);
        _tick.Start();
        Log.Info($"Thumbnail shown for {item.Source} at {window.CurrentRect}");
        ToastShown?.Invoke(window);
        return window;
    }

    public void DismissAll()
    {
        foreach (var e in _entries.Where(e => !e.Window.IsDragging).ToList()) Dismiss(e, fade: false);
    }

    private void Wire(Entry entry)
    {
        var w = entry.Window;
        w.HoverChanged += (_, hovered) =>
        {
            entry.Hovered = hovered;
            if (hovered) Pause(entry); else Resume(entry);
        };
        w.DragStarting += _ => Pause(entry);
        w.DragFinished += (_, result) =>
        {
            if (result == FileDragResult.Dropped)
            {
                // Like macOS: once the screenshot has been dropped into an app, the thumbnail goes away.
                Dismiss(entry, fade: true);
            }
            else
            {
                entry.Hovered = w.IsMouseOver;
                if (!entry.Hovered) Resume(entry);
            }
        };
        w.DismissRequested += _ => Dismiss(entry, fade: false);
        w.OpenRequested += _ =>
        {
            OpenRequested?.Invoke(w.Item);
            Dismiss(entry, fade: true);
        };
        w.MenuRequested += (_, p) => ShowMenu(entry, p);
        w.PrepareDrag = item =>
        {
            var path = item.WaitForFile(TimeSpan.FromSeconds(5));
            if (path is null || !File.Exists(path)) return (null, null);
            return (path, _store.Use(path));
        };
        w.Closed += (_, _) =>
        {
            _entries.Remove(entry);
            ToastClosed?.Invoke(w);
            if (_entries.Count == 0) _tick.Stop();
        };
    }

    private static void Pause(Entry e)
    {
        if (e.PausedRemaining is not null) return;
        e.PausedRemaining = e.Deadline - DateTime.UtcNow;
    }

    private static void Resume(Entry e)
    {
        if (e.PausedRemaining is not { } remaining || e.Window.IsDragging) return;
        // Give the user a moment after moving away, as macOS does.
        e.Deadline = DateTime.UtcNow + (remaining < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : remaining);
        e.PausedRemaining = null;
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var e in _entries.ToList())
        {
            if (e.Closing || e.PausedRemaining is not null || e.Hovered || e.Window.IsDragging) continue;
            if (now >= e.Deadline) Dismiss(e, fade: false);
        }
    }

    private void Dismiss(Entry e, bool fade)
    {
        if (e.Closing || e.Window.IsDragging) return;
        e.Closing = true;
        Log.Info($"Thumbnail dismissed ({(fade ? "after drop/open" : "timeout or user")})");
        var monitor = e.Window.Monitor.Handle;
        e.Window.Exit(AnimationFactor, fade);
        Restack(monitor, except: null);
    }

    // ------------------------------------------------------------------ layout

    private Dictionary<ScreenshotToastWindow, RECT> Layout(IntPtr monitorHandle)
    {
        var list = _entries.Where(e => !e.Closing && e.Window.Monitor.Handle == monitorHandle).ToList();
        var result = new Dictionary<ScreenshotToastWindow, RECT>();
        if (list.Count == 0) return result;
        var mon = list[0].Window.Monitor;
        var s = mon.DpiScale;
        var work = new RectD(mon.WorkArea.Left / s, mon.WorkArea.Top / s, mon.WorkArea.Width / s, mon.WorkArea.Height / s);
        var positions = ToastStackLayout.Arrange(work, list.Select(e => (e.Window.CardWidth, e.Window.CardHeight)).ToList());
        for (var i = 0; i < list.Count; i++)
        {
            var (x, y, fits) = positions[i];
            var entry = list[i];
            if (!fits && !entry.Window.IsDragging) { _dispatcher.BeginInvoke(() => Dismiss(entry, fade: false)); continue; }
            result[list[i].Window] = list[i].Window.RectFor(x, y);
        }
        return result;
    }

    private void Restack(IntPtr monitorHandle, ScreenshotToastWindow? except)
    {
        foreach (var (window, target) in Layout(monitorHandle))
        {
            if (window == except || window.IsDragging || !window.IsVisible) continue;
            AnimateTo(window, target);
        }
    }

    /// <summary>Moves a toast window smoothly (window position can't be animated by WPF directly).</summary>
    private void AnimateTo(ScreenshotToastWindow window, RECT target)
    {
        var start = window.CurrentRect;
        if (start.Left == target.Left && start.Top == target.Top) return;
        var factor = AnimationFactor;
        if (factor <= 0) { window.ApplyRect(target); return; }
        var sw = Stopwatch.StartNew();
        var duration = 220 * factor;
        var timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, sw.ElapsedMilliseconds / duration);
            var e = 1 - Math.Pow(1 - t, 3);
            int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * e);
            window.ApplyRect(new RECT(Lerp(start.Left, target.Left), Lerp(start.Top, target.Top), Lerp(start.Right, target.Right), Lerp(start.Bottom, target.Bottom)));
            if (t >= 1 || !window.IsVisible) timer.Stop();
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ context menu

    private void ShowMenu(Entry entry, POINT p)
    {
        Pause(entry);
        var item = entry.Window.Item;
        var menu = new NativeMenu()
            .Add(MenuOpen, "Open", isDefault: true)
            .Add(MenuEdit, "Edit…")
            .AddSeparator()
            .Add(MenuCopy, "Copy");
        if (item.IsTemporary) menu.Add(MenuSave, "Save to Screenshots folder");
        menu.Add(MenuSaveAs, "Save As…")
            .Add(MenuShow, "Show in folder")
            .AddSeparator()
            .Add(MenuDismiss, "Dismiss");

        var cmd = menu.Show(entry.Window.Handle, p.X, p.Y, ThemeService.IsDark(_settings().Theme), alignAboveCursor: true);
        try
        {
            switch (cmd)
            {
                case MenuOpen:
                    if (item.WaitForFile(TimeSpan.FromSeconds(5)) is { } open)
                        Process.Start(new ProcessStartInfo(open) { UseShellExecute = true })?.Dispose();
                    Dismiss(entry, fade: true);
                    return;
                case MenuEdit:
                    OpenRequested?.Invoke(item);
                    Dismiss(entry, fade: true);
                    return;
                case MenuCopy:
                    ScreenshotClipboard.Copy(item.Image, item.WaitForFile(TimeSpan.FromSeconds(5)));
                    break;
                case MenuSave:
                    _store.MakePermanent(item);
                    break;
                case MenuSaveAs:
                    if (ScreenshotClipboard.SaveAs(item.Image, item.WaitForFile(TimeSpan.FromSeconds(5))) is not null)
                        Dismiss(entry, fade: true);
                    break;
                case MenuShow:
                    if (item.WaitForFile(TimeSpan.FromSeconds(5)) is { } shown)
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{shown}\"") { UseShellExecute = true })?.Dispose();
                    break;
                case MenuDismiss:
                    Dismiss(entry, fade: false);
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Screenshot menu action failed", ex);
        }
        entry.Hovered = entry.Window.IsMouseOver;
        if (!entry.Hovered) Resume(entry);
    }

    public void Dispose()
    {
        _tick.Stop();
        foreach (var e in _entries.ToList()) e.Window.Close();
    }
}
