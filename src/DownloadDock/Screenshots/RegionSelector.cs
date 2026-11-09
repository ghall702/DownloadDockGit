using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DownloadDock.Interop;

namespace DownloadDock.Screenshots;

/// <summary>Result of a region selection: the cropped pixels and the monitor they came from.</summary>
internal sealed record RegionCapture(BitmapSource Image, IntPtr Monitor, RECT ScreenRect);

/// <summary>
/// macOS-style rectangular capture. The whole desktop is frozen first (so menus and hover states can be
/// captured and the overlay itself never appears in the image), then one dimmed overlay per monitor lets
/// the user drag a rectangle with a crosshair. Esc or right-click cancels. Coordinates are mapped through
/// each overlay's own stretch, so mixed DPI and negative multi-monitor coordinates are handled exactly.
/// </summary>
internal sealed class RegionSelector
{
    private readonly HotkeyService? _hotkeys;
    private readonly List<RegionOverlayWindow> _overlays = new();
    private TaskCompletionSource<RegionCapture?>? _tcs;
    private IntPtr _previousForeground;

    public RegionSelector(HotkeyService? hotkeys) => _hotkeys = hotkeys;

    public bool IsActive => _tcs is not null;

    /// <summary>Raised when the overlays are on screen (tests use it to start injecting input).</summary>
    public event Action? Shown;

    public Task<RegionCapture?> SelectAsync()
    {
        if (_tcs is not null) return _tcs.Task;
        _tcs = new TaskCompletionSource<RegionCapture?>();
        _previousForeground = NativeMethods.GetForegroundWindow();

        var virtualRect = ScreenCapture.VirtualScreen();
        var frozen = ScreenCapture.Capture(virtualRect);
        foreach (var monitor in ScreenCapture.Monitors())
        {
            var crop = ScreenCapture.Crop(frozen, new Int32Rect(
                monitor.Bounds.Left - virtualRect.Left, monitor.Bounds.Top - virtualRect.Top, monitor.Bounds.Width, monitor.Bounds.Height));
            var overlay = new RegionOverlayWindow(crop, monitor);
            overlay.Selected += OnSelected;
            overlay.Cancelled += Cancel;
            overlay.SelectionStarted += o => { foreach (var other in _overlays) if (other != o) other.IsEnabled = false; };
            _overlays.Add(overlay);
            overlay.ShowOnMonitor();
        }

        if (_hotkeys is not null) _hotkeys.EscapeOverride = Cancel;

        // Give keyboard focus to the overlay under the cursor (Esc is also caught by the hook as a fallback).
        NativeMethods.GetCursorPos(out var cursor);
        var active = _overlays.FirstOrDefault(o => Contains(o.Monitor.Bounds, cursor)) ?? _overlays.FirstOrDefault();
        if (active is not null)
        {
            active.Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(active).Handle);
        }
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => Shown?.Invoke());
        return _tcs.Task;
    }

    public void Cancel() => Finish(null);

    private void OnSelected(RegionOverlayWindow overlay, Int32Rect pixels)
    {
        var image = ScreenCapture.Crop(overlay.Frozen, pixels);
        var rect = new RECT(overlay.Monitor.Bounds.Left + pixels.X, overlay.Monitor.Bounds.Top + pixels.Y,
            overlay.Monitor.Bounds.Left + pixels.X + pixels.Width, overlay.Monitor.Bounds.Top + pixels.Y + pixels.Height);
        Finish(new RegionCapture(image, overlay.Monitor.Handle, rect));
    }

    private void Finish(RegionCapture? result)
    {
        var tcs = _tcs;
        if (tcs is null) return;
        _tcs = null;
        if (_hotkeys is not null) _hotkeys.EscapeOverride = null;
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
        // Hand focus back to whatever the user was working in.
        if (_previousForeground != IntPtr.Zero) NativeMethods.SetForegroundWindow(_previousForeground);
        tcs.TrySetResult(result);
    }

    private static bool Contains(RECT r, POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}

/// <summary>A full-monitor overlay showing the frozen screen, dimmed outside the selection.</summary>
internal sealed class RegionOverlayWindow : Window
{
    private const double MinSelection = 4; // physical px; smaller drags are treated as a click
    private readonly Path _dim;
    private readonly Rectangle _border;
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText;
    private readonly Grid _root;
    private Point? _start;
    private bool _repositionPending;

    public BitmapSource Frozen { get; }
    public MonitorInfo Monitor { get; }

    public event Action<RegionOverlayWindow, Int32Rect>? Selected;
    public event Action? Cancelled;
    public event Action<RegionOverlayWindow>? SelectionStarted;

    public RegionOverlayWindow(BitmapSource frozen, MonitorInfo monitor)
    {
        Frozen = frozen;
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "DownloadDock capture";
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;

        _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), IsHitTestVisible = false };
        _border = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        _sizeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 3),
            Child = _sizeText,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 7),
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Drag to capture an area  ·  Esc to cancel",
                Foreground = Brushes.White,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            },
        };

        _root = new Grid { Background = new ImageBrush(frozen) { Stretch = Stretch.Fill } };
        _root.Children.Add(_dim);
        _root.Children.Add(_border);
        _root.Children.Add(_sizeLabel);
        _root.Children.Add(hint);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)(ex | NativeMethods.WS_EX_TOOLWINDOW));
        };
        // Moving onto a monitor with another DPI can make WPF resize us; re-cover the monitor once, deferred
        // (never from inside the DPI message itself).
        DpiChanged += (_, e) =>
        {
            if (Math.Abs(e.NewDpi.DpiScaleX - e.OldDpi.DpiScaleX) < 0.001 || _repositionPending) return;
            _repositionPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _repositionPending = false; CoverMonitor(); });
        };
        SizeChanged += (_, _) => UpdateDim(null);
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Cancelled?.Invoke();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancelled?.Invoke();
        };
    }

    public void ShowOnMonitor()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        CoverMonitor();
        Show();
        CoverMonitor();
    }

    private void CoverMonitor()
    {
        var b = Monitor.Bounds;
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0);
    }

    /// <summary>Physical pixels per DIP for this overlay (the frozen image is stretched over the window).</summary>
    private (double X, double Y) PixelScale()
        => (Monitor.Bounds.Width / Math.Max(1, _root.ActualWidth), Monitor.Bounds.Height / Math.Max(1, _root.ActualHeight));

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_root);
        CaptureMouse();
        SelectionStarted?.Invoke(this);
        UpdateDim(new Rect(_start.Value, _start.Value));
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is null) return;
        var p = e.GetPosition(_root);
        p = new Point(Math.Clamp(p.X, 0, _root.ActualWidth), Math.Clamp(p.Y, 0, _root.ActualHeight));
        UpdateDim(new Rect(_start.Value, p));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        ReleaseMouseCapture();
        var p = e.GetPosition(_root);
        p = new Point(Math.Clamp(p.X, 0, _root.ActualWidth), Math.Clamp(p.Y, 0, _root.ActualHeight));
        var px = ToPixels(new Rect(_start.Value, p));
        _start = null;
        if (px.Width < MinSelection || px.Height < MinSelection)
        {
            // A click rather than a drag: keep waiting for a proper selection.
            UpdateDim(null);
            return;
        }
        Selected?.Invoke(this, px);
    }

    internal Int32Rect ToPixels(Rect dip)
    {
        var (sx, sy) = PixelScale();
        var x = (int)Math.Round(dip.X * sx);
        var y = (int)Math.Round(dip.Y * sy);
        var r = (int)Math.Round(dip.Right * sx);
        var b = (int)Math.Round(dip.Bottom * sy);
        return ScreenCapture.Clamp(new Int32Rect(x, y, Math.Max(0, r - x), Math.Max(0, b - y)), Monitor.Bounds.Width, Monitor.Bounds.Height);
    }

    private void UpdateDim(Rect? selection)
    {
        var full = new RectangleGeometry(new Rect(0, 0, _root.ActualWidth, _root.ActualHeight));
        if (selection is not { } sel || sel.Width <= 0 || sel.Height <= 0)
        {
            _dim.Data = full;
            _border.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(sel));
        _border.Visibility = Visibility.Visible;
        _border.Margin = new Thickness(sel.X - 0.5, sel.Y - 0.5, 0, 0);
        _border.Width = sel.Width + 1;
        _border.Height = sel.Height + 1;
        var px = ToPixels(sel);
        _sizeText.Text = string.Create(CultureInfo.InvariantCulture, $"{px.Width} × {px.Height}");
        _sizeLabel.Visibility = Visibility.Visible;
        var labelX = Math.Min(sel.Right + 8, _root.ActualWidth - 90);
        var labelY = Math.Min(sel.Bottom + 8, _root.ActualHeight - 28);
        _sizeLabel.Margin = new Thickness(Math.Max(0, labelX), Math.Max(0, labelY), 0, 0);
    }
}
