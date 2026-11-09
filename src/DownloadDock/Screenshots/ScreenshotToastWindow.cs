using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DownloadDock.Dragging;
using DownloadDock.Interop;

namespace DownloadDock.Screenshots;

/// <summary>
/// The macOS-style floating screenshot thumbnail. It never takes focus (WS_EX_NOACTIVATE +
/// MA_NOACTIVATE), slides in from the screen edge, can be dragged straight into any app as the real PNG
/// file, swiped right to dismiss, clicked to open the editor, and right-clicked for more actions.
/// </summary>
internal sealed class ScreenshotToastWindow : Window
{
    public const double ShadowPad = 22;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private readonly Border _card;
    private readonly TranslateTransform _slide = new();
    private Point? _pressPoint;
    private bool _swiping;
    private bool _dragging;
    private RECT _targetRect;

    public ScreenshotItem Item { get; }
    public double CardWidth { get; }
    public double CardHeight { get; }
    public MonitorInfo Monitor { get; set; }

    /// <summary>Dismissed by the user (swipe) – the manager animates out and closes.</summary>
    public event Action<ScreenshotToastWindow>? DismissRequested;
    public event Action<ScreenshotToastWindow>? OpenRequested;
    public event Action<ScreenshotToastWindow, POINT>? MenuRequested;
    public event Action<ScreenshotToastWindow, bool>? HoverChanged;
    public event Action<ScreenshotToastWindow>? DragStarting;
    public event Action<ScreenshotToastWindow, FileDragResult>? DragFinished;

    /// <summary>Supplies the file to drag (waits for the PNG if it's still being written) and marks it in use.</summary>
    public Func<ScreenshotItem, (string? Path, IDisposable? Lease)>? PrepareDrag { get; set; }

    public bool IsDragging => _dragging;

    public ScreenshotToastWindow(ScreenshotItem item, MonitorInfo monitor, bool dark)
    {
        Item = item;
        Monitor = monitor;
        (CardWidth, CardHeight) = Core.Screenshots.ToastStackLayout.FitThumbnail(item.Image.PixelWidth, item.Image.PixelHeight);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Title = "Screenshot";
        Left = -32000;
        Top = -32000;
        Width = CardWidth + 2 * ShadowPad;
        Height = CardHeight + 2 * ShadowPad;
        UseLayoutRounding = true;

        var thumb = Downscale(item.Image, CardWidth * monitor.DpiScale * 2);
        _card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(ShadowPad),
            Cursor = Cursors.Hand,
            RenderTransform = _slide,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.42, RenderingBias = RenderingBias.Performance },
            ToolTip = "Drag into any app · Click to edit · Swipe right to dismiss",
        };
        RenderOptions.SetBitmapScalingMode(_card, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(_card, "Screenshot thumbnail");
        Content = new Grid { Children = { _card } };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
            // Keep thumbnails out of later screenshots (ours and Windows'), as on macOS.
            ScreenshotNative.SetWindowDisplayAffinity(hwnd, ScreenshotNative.WDA_EXCLUDEFROMCAPTURE);
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
                return IntPtr.Zero;
            });
        };
        DpiChanged += (_, e) =>
        {
            if (Math.Abs(e.NewDpi.DpiScaleX - e.OldDpi.DpiScaleX) > 0.001)
                Dispatcher.BeginInvoke(() => ApplyRect(_targetRect));
        };

        _card.MouseEnter += (_, _) => HoverChanged?.Invoke(this, true);
        _card.MouseLeave += (_, _) => { if (!_dragging) HoverChanged?.Invoke(this, false); };
        _card.MouseLeftButtonDown += OnDown;
        _card.MouseMove += OnMove;
        _card.MouseLeftButtonUp += OnUp;
        _card.MouseRightButtonUp += (_, e) =>
        {
            NativeMethods.GetCursorPos(out var p);
            e.Handled = true;
            MenuRequested?.Invoke(this, p);
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Places the card's top-left at a DIP position of its monitor (stack layout coordinates).</summary>
    public RECT RectFor(double cardX, double cardY)
    {
        var s = Monitor.DpiScale;
        return new RECT(
            (int)Math.Round((cardX - ShadowPad) * s), (int)Math.Round((cardY - ShadowPad) * s),
            (int)Math.Round((cardX + CardWidth + ShadowPad) * s), (int)Math.Round((cardY + CardHeight + ShadowPad) * s));
    }

    public RECT CurrentRect => _targetRect;

    public void ApplyRect(RECT r)
    {
        _targetRect = r;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Shows without activating and slides the card in from the right screen edge.</summary>
    public void ShowAt(RECT r, double animationFactor)
    {
        ApplyRect(r);
        Show();
        ApplyRect(r);
        // Distance to the monitor's right edge, so the card appears to come out of the screen edge.
        var fromX = (Monitor.Bounds.Right - r.Left) / Monitor.DpiScale;
        if (animationFactor <= 0) return;
        _slide.X = fromX;
        var anim = new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(380 * animationFactor))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>Slides the card out to the right (or fades if it was dropped somewhere) and closes.</summary>
    public void Exit(double animationFactor, bool fade = false)
    {
        if (animationFactor <= 0) { Close(); return; }
        var duration = TimeSpan.FromMilliseconds(260 * animationFactor);
        if (fade)
        {
            var f = new DoubleAnimation(0, duration);
            f.Completed += (_, _) => Close();
            _card.BeginAnimation(OpacityProperty, f);
            return;
        }
        var toX = (Monitor.Bounds.Right - _targetRect.Left) / Monitor.DpiScale;
        var anim = new DoubleAnimation(toX, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => Close();
        _slide.BeginAnimation(TranslateTransform.XProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    // ------------------------------------------------------------------ pointer

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _swiping = false;
        _card.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || _dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { ResetPress(); return; }
        var p = e.GetPosition(this);
        var dx = p.X - start.X;
        var dy = p.Y - start.Y;

        if (_swiping)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = Math.Max(0, dx);
            return;
        }

        var scale = Monitor.DpiScale;
        var thresholdX = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG)) / scale;
        var thresholdY = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG)) / scale;
        if (Math.Abs(dx) < thresholdX && Math.Abs(dy) < thresholdY) return;

        if (dx > 0 && Math.Abs(dx) > 1.6 * Math.Abs(dy))
        {
            // Rightwards towards the screen edge = swipe to dismiss (as on macOS).
            _swiping = true;
            return;
        }
        StartDrag(start);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressPoint is null) return;
        var wasSwiping = _swiping;
        var dx = e.GetPosition(this).X - _pressPoint.Value.X;
        ResetPress();
        if (wasSwiping)
        {
            if (dx > 40) DismissRequested?.Invoke(this);
            else _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() });
            return;
        }
        if (!_dragging) OpenRequested?.Invoke(this);
    }

    private void ResetPress()
    {
        _pressPoint = null;
        _swiping = false;
        if (_card.IsMouseCaptured) _card.ReleaseMouseCapture();
    }

    private void StartDrag(Point pressInWindow)
    {
        if (_card.IsMouseCaptured) _card.ReleaseMouseCapture();
        _pressPoint = null;
        var (path, lease) = PrepareDrag?.Invoke(Item) ?? (null, null);
        if (path is null) return;

        _dragging = true;
        DragStarting?.Invoke(this);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var image = FileDragSource.CreateDragImage(_card.Background is ImageBrush b ? b.ImageSource : Item.Image, Math.Max(CardWidth, CardHeight), dpi, isIcon: false);
        var offset = new Point((pressInWindow.X - ShadowPad + 6) * dpi, (pressInWindow.Y - ShadowPad + 6) * dpi);

        _card.Opacity = 0; // the drag image *is* the thumbnail now, like macOS
        FileDragResult result;
        try
        {
            result = FileDragSource.DoDragDrop(Handle, new[] { path }, image, offset, out _);
        }
        catch (Exception ex)
        {
            Services.Log.Error("Screenshot drag failed", ex);
            result = FileDragResult.Failed;
        }
        finally
        {
            _dragging = false;
            lease?.Dispose();
        }
        if (result != FileDragResult.Dropped)
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        DragFinished?.Invoke(this, result);
    }

    private static BitmapSource Downscale(BitmapSource source, double targetWidth)
    {
        if (source.PixelWidth <= targetWidth * 1.2) return source;
        var s = targetWidth / source.PixelWidth;
        var scaled = new TransformedBitmap(source, new ScaleTransform(s, s));
        var copy = new WriteableBitmap(scaled);
        copy.Freeze();
        return copy;
    }
}
