using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Fan;
using DownloadDock.Core.Settings;
using DownloadDock.Dragging;
using DownloadDock.Interop;
using DownloadDock.Thumbnails;
using DownloadDock.ViewModels;

namespace DownloadDock.Fan;

/// <summary>
/// The Downloads fan: a transparent, borderless, topmost window whose items are individually
/// positioned, rotated and z-ordered along the arc computed by <see cref="FanLayoutCalculator"/>.
/// Handles hover, click-to-open, threshold-based native drag, wheel overflow paging, keyboard
/// navigation and dismissal (never while a drag is in progress).
/// </summary>
internal partial class FanWindow
{
    public const double LabelWidth = 230;
    public const double LabelGap = 10;
    private const double HoverScaleFactor = 1.08;

    private readonly ThumbnailService _thumbnails;
    private readonly List<FanItemView> _views = new();
    private readonly FanPager _pager = new();
    private readonly DispatcherTimer _closeTimer;

    private IReadOnlyList<DownloadEntry> _entries = Array.Empty<DownloadEntry>();
    private int _totalCount;
    private string _folder = "";
    private AppSettings _settings = new();
    private FanAnchor? _anchor;
    private FanLayoutResult? _layout;
    private Point _origin;          // DIP position of the window's top-left in the layout space
    private double _windowScale = 1; // DIP → px for the window's monitor
    private bool _pendingRefresh;

    private FanItemView? _pressed;
    private Point _pressWindowPoint;
    private Point _pressItemPoint;
    private bool _dragStarted;
    private FanItemView? _highlighted;

    /// <summary>A file or the folder item was clicked.</summary>
    public event Action<FanItemViewModel>? OpenRequested;

    /// <summary>Right-click / menu key on an item. Point is in physical screen pixels.</summary>
    public event Action<FanItemViewModel, POINT>? ContextMenuRequested;

    /// <summary>Fired after the fan has fully closed.</summary>
    public event Action? FanClosed;

    /// <summary>Fired when a drag finishes (for diagnostics / status).</summary>
    public event Action<FanItemViewModel, FileDragResult, int>? DragFinished;

    public bool IsOpen { get; private set; }
    public bool IsClosing { get; private set; }
    public bool IsDragging { get; private set; }
    public DateTime LastClosedUtc { get; private set; } = DateTime.MinValue;

    public FanWindow(ThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
        InitializeComponent();
        _closeTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
        _closeTimer.Tick += (_, _) => FinishClose();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex = (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        };
        Deactivated += OnDeactivated;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        MouseWheel += OnMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        DpiChanged += (_, _) =>
        {
            if (IsOpen && !IsClosing && !IsDragging) Relayout(animate: false);
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    // ------------------------------------------------------------------ public API

    public void Open(FanAnchor anchor, IReadOnlyList<DownloadEntry> entries, int totalCount, string folder, AppSettings settings, bool dark)
    {
        _closeTimer.Stop();
        _anchor = anchor;
        _entries = entries;
        _totalCount = totalCount;
        _folder = folder;
        _settings = settings;
        _pager.Reset();
        ApplyTheme(dark);

        IsOpen = true;
        IsClosing = false;
        Relayout(animate: false, buildOnly: true);

        Show();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        Focus();

        // If WPF picked up a different DPI than the target monitor's, re-place with the real one.
        var actual = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Math.Abs(actual - anchor.DpiScale) > 0.01)
            Relayout(animate: false, buildOnly: true);

        AnimateOpen();
    }

    /// <summary>Called when the Downloads folder changes while the fan may be open.</summary>
    public void UpdateEntries(IReadOnlyList<DownloadEntry> entries, int totalCount, string folder)
    {
        _entries = entries;
        _totalCount = totalCount;
        _folder = folder;
        if (!IsOpen || IsClosing) return;
        if (IsDragging || _pressed is not null)
        {
            _pendingRefresh = true; // never disturb an active press or drag
            return;
        }
        Relayout(animate: true);
    }

    public void ApplyTheme(bool dark)
    {
        static SolidColorBrush B(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }
        Resources["Fan.LabelBackground"] = dark ? B(0xE6262626) : B(0xF2FAFAFA);
        Resources["Fan.LabelForeground"] = dark ? B(0xFFFFFFFF) : B(0xFF1A1A1A);
        Resources["Fan.LabelBorder"] = dark ? B(0x26FFFFFF) : B(0x1F000000);
        Resources["Fan.CardBorder"] = dark ? B(0x33FFFFFF) : B(0x26000000);
        Resources["Fan.PlaceholderBackground"] = dark ? B(0xFF2F2F2F) : B(0xFFFFFFFF);
        Resources["Fan.PlaceholderForeground"] = dark ? B(0x99FFFFFF) : B(0x66000000);
        var accent = Services.ThemeService.AccentColor();
        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        Resources["Fan.Accent"] = accentBrush;
        var lum = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255;
        Resources["Fan.AccentForeground"] = lum > 0.6 ? B(0xFF000000) : B(0xFFFFFFFF);
    }

    public void CloseFan(bool animate = true)
    {
        if (!IsOpen || IsClosing || IsDragging) return;
        IsClosing = true;
        ReleasePress();
        SetHighlight(null);

        var factor = AppSettings.AnimationFactor(_settings.AnimationSpeed);
        if (!animate || factor <= 0 || _anchor is null)
        {
            FinishClose();
            return;
        }

        var anchorLocal = AnchorLocal();
        var duration = TimeSpan.FromMilliseconds(170 * factor);
        var maxSlot = _views.Count == 0 ? 0 : _views.Max(v => v.SlotIndex);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        foreach (var v in _views)
        {
            var center = ViewCenter(v);
            var begin = TimeSpan.FromMilliseconds((maxSlot - v.SlotIndex) * 9 * factor);
            To(v.Offset, TranslateTransform.XProperty, anchorLocal.X - center.X, duration, begin, ease);
            To(v.Offset, TranslateTransform.YProperty, anchorLocal.Y - center.Y, duration, begin, ease);
            To(v.Rotation, RotateTransform.AngleProperty, 0, duration, begin, ease);
            To(v.OpenScale, ScaleTransform.ScaleXProperty, 0.3, duration, begin, ease);
            To(v.OpenScale, ScaleTransform.ScaleYProperty, 0.3, duration, begin, ease);
            To(v, OpacityProperty, 0, duration, begin, ease);
        }
        _closeTimer.Interval = duration + TimeSpan.FromMilliseconds(maxSlot * 9 * factor + 20);
        _closeTimer.Start();
    }

    // ------------------------------------------------------------------ layout

    private void Relayout(bool animate, bool buildOnly = false)
    {
        if (_anchor is null) return;
        var anchor = _anchor;
        var s = (double)_settings.ThumbnailSize;
        var options = new FanLayoutOptions
        {
            ThumbnailSize = s,
            LabelWidth = LabelWidth,
            LabelGap = LabelGap,
            HoverScale = HoverScaleFactor,
        };

        var fileCount = Math.Min(_settings.VisibleFiles, _entries.Count);
        _layout = FanLayoutCalculator.Calculate(fileCount + 1, anchor.AnchorXDip, anchor.AnchorEdgeYDip, anchor.WorkAreaDip, anchor.Direction, options);
        var capacity = Math.Max(0, _layout.FittedCount - 1);
        _pager.Update(_entries.Count, capacity);

        // Window rectangle: layout bounds clipped to the monitor (the fan may overlap the
        // transparent area above the taskbar, never another monitor).
        var scale = anchor.DpiScale;
        var mon = new RectD(anchor.Monitor.Left / scale, anchor.Monitor.Top / scale, anchor.Monitor.Width / scale, anchor.Monitor.Height / scale);
        var b = _layout.Bounds;
        var left = Math.Max(b.Left, mon.Left);
        var top = Math.Max(b.Top, mon.Top);
        var right = Math.Min(b.Right, mon.Right);
        var bottom = Math.Min(b.Bottom, mon.Bottom);
        _origin = new Point(left, top);
        _windowScale = scale;

        RebuildViews(animateSwap: animate && !buildOnly);

        Width = right - left;
        Height = bottom - top;
        var hwnd = Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            (int)Math.Round(left * scale), (int)Math.Round(top * scale),
            (int)Math.Ceiling((right - left) * scale), (int)Math.Ceiling((bottom - top) * scale),
            NativeMethods.SWP_NOACTIVATE);
    }

    private void RebuildViews(bool animateSwap)
    {
        var previousHighlightPath = _highlighted?.ViewModel.Path;
        foreach (var v in _views) DetachView(v);
        _views.Clear();
        ItemsCanvas.Children.Clear();
        _highlighted = null;
        if (_layout is null) return;

        var s = (double)_settings.ThumbnailSize;
        var px = ThumbnailPixels();
        var (start, count) = _pager.VisibleRange;
        var placements = _layout.Items;

        for (var slot = 0; slot < placements.Count; slot++)
        {
            var p = placements[slot];
            FanItemViewModel vm;
            if (slot == placements.Count - 1)
            {
                vm = CreateFolderItem(s);
            }
            else
            {
                var index = start + slot;
                if (slot >= count || index >= _entries.Count) continue;
                vm = CreateFileItem(_entries[index], s, px);
            }

            var view = new FanItemView(vm, LabelWidth, LabelGap) { SlotIndex = slot, TargetAngle = p.AngleDegrees };
            Canvas.SetLeft(view, p.CenterX - _origin.X - view.PivotX);
            Canvas.SetTop(view, p.CenterY - _origin.Y - view.ItemHeight / 2);
            Panel.SetZIndex(view, p.ZIndex);
            view.Rotation.Angle = p.AngleDegrees;
            AttachView(view);
            _views.Add(view);
            ItemsCanvas.Children.Add(view);

            if (animateSwap && AppSettings.AnimationFactor(_settings.AnimationSpeed) > 0)
            {
                var d = TimeSpan.FromMilliseconds(140);
                From(view, OpacityProperty, 0.55, 1, d, TimeSpan.Zero, new QuadraticEase());
            }
            if (previousHighlightPath is not null && string.Equals(vm.Path, previousHighlightPath, StringComparison.OrdinalIgnoreCase) && view.IsMouseOver)
                SetHighlight(view);
        }
    }

    private FanItemViewModel CreateFolderItem(double s)
    {
        var vm = new FanItemViewModel(s)
        {
            IsFolderItem = true,
            FolderPath = _folder,
            DisplayName = "Open Downloads Folder",
        };
        vm.Subtitle = OverflowText();
        var px = ThumbnailPixels();
        _thumbnails.GetIconAsync(_folder, px).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } img)
                Dispatcher.BeginInvoke(() => vm.SetThumbnail(img, isIcon: true));
        }, TaskScheduler.Default);
        return vm;
    }

    private string? OverflowText()
    {
        if (_entries.Count == 0) return "No downloads yet";
        var older = _pager.HiddenOlder + Math.Max(0, _totalCount - _entries.Count);
        var newer = _pager.HiddenNewer;
        if (older > 0 && newer > 0) return $"{newer} newer · {older} older — scroll";
        if (older > 0) return $"{older} more — scroll for older";
        if (newer > 0) return $"{newer} newer — scroll back";
        return null;
    }

    private FanItemViewModel CreateFileItem(DownloadEntry entry, double s, int px)
    {
        var vm = new FanItemViewModel(s)
        {
            Entry = entry,
            DisplayName = FanItemViewModel.FormatName(entry.Name, _settings.ShowFileExtensions),
        };
        if (_thumbnails.TryGetCached(entry, px) is { } cached)
        {
            vm.SetThumbnail(cached.Image, cached.IsIcon);
        }
        else
        {
            _thumbnails.GetAsync(entry, px).ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is { } r)
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (vm.Entry == entry) vm.SetThumbnail(r.Image, r.IsIcon);
                    });
            }, TaskScheduler.Default);
        }
        return vm;
    }

    /// <summary>Thumbnail resolution in physical pixels (crisp on high-DPI screens, with hover headroom).</summary>
    public int ThumbnailPixels() => ThumbnailPixelsFor(_settings.ThumbnailSize, _anchor?.DpiScale ?? 1);

    public static int ThumbnailPixelsFor(int thumbnailSize, double dpiScale)
        => (int)Math.Ceiling(thumbnailSize * Math.Max(1, dpiScale) * HoverScaleFactor / 16.0) * 16;

    // ------------------------------------------------------------------ animation

    private void AnimateOpen()
    {
        var factor = AppSettings.AnimationFactor(_settings.AnimationSpeed);
        if (factor <= 0) return;
        var anchorLocal = AnchorLocal();
        var moveEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var v in _views)
        {
            var center = ViewCenter(v);
            var begin = TimeSpan.FromMilliseconds(v.SlotIndex * 18 * factor);
            var duration = TimeSpan.FromMilliseconds((250 + v.SlotIndex * 10) * factor);
            From(v.Offset, TranslateTransform.XProperty, anchorLocal.X - center.X, 0, duration, begin, moveEase);
            From(v.Offset, TranslateTransform.YProperty, anchorLocal.Y - center.Y, 0, duration, begin, moveEase);
            From(v.Rotation, RotateTransform.AngleProperty, 0, v.TargetAngle, duration, begin, ease);
            From(v.OpenScale, ScaleTransform.ScaleXProperty, 0.3, 1, duration, begin, ease);
            From(v.OpenScale, ScaleTransform.ScaleYProperty, 0.3, 1, duration, begin, ease);
            From(v, OpacityProperty, 0, 1, TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.55), begin, new QuadraticEase());
        }
    }

    /// <summary>Animates from → to, holding <paramref name="from"/> during the stagger delay, then commits <paramref name="to"/> as the base value.</summary>
    private static void From(DependencyObject target, DependencyProperty dp, double from, double to, TimeSpan duration, TimeSpan begin, IEasingFunction ease)
    {
        var animatable = (IAnimatable)target;
        animatable.BeginAnimation(dp, null);
        target.SetValue(dp, from);
        var anim = new DoubleAnimation(from, to, duration) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        anim.Completed += (_, _) =>
        {
            target.SetValue(dp, to);
            animatable.BeginAnimation(dp, null);
        };
        animatable.BeginAnimation(dp, anim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Animates from the current (possibly animated) value to <paramref name="to"/>.</summary>
    private static void To(DependencyObject target, DependencyProperty dp, double to, TimeSpan duration, TimeSpan begin, IEasingFunction ease)
    {
        var anim = new DoubleAnimation(to, duration) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        ((IAnimatable)target).BeginAnimation(dp, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private Point AnchorLocal() => _anchor is null
        ? new Point(Width / 2, Height)
        : new Point(_anchor.AnchorXDip - _origin.X, _anchor.AnchorEdgeYDip - _origin.Y);

    private static Point ViewCenter(FanItemView v) => new(Canvas.GetLeft(v) + v.PivotX, Canvas.GetTop(v) + v.ItemHeight / 2);

    private void FinishClose()
    {
        _closeTimer.Stop();
        Hide();
        foreach (var v in _views) DetachView(v);
        _views.Clear();
        ItemsCanvas.Children.Clear();
        _highlighted = null;
        IsOpen = false;
        IsClosing = false;
        _pendingRefresh = false;
        LastClosedUtc = DateTime.UtcNow;
        FanClosed?.Invoke();
    }

    // ------------------------------------------------------------------ hover & highlight

    private void AttachView(FanItemView v)
    {
        v.MouseEnter += OnItemMouseEnter;
        v.MouseLeave += OnItemMouseLeave;
        v.MouseLeftButtonDown += OnItemMouseLeftButtonDown;
        v.MouseRightButtonUp += OnItemMouseRightButtonUp;
    }

    private void DetachView(FanItemView v)
    {
        v.MouseEnter -= OnItemMouseEnter;
        v.MouseLeave -= OnItemMouseLeave;
        v.MouseLeftButtonDown -= OnItemMouseLeftButtonDown;
        v.MouseRightButtonUp -= OnItemMouseRightButtonUp;
        v.Detach();
    }

    private void OnItemMouseEnter(object sender, MouseEventArgs e)
    {
        if (IsClosing || IsDragging) return;
        if (_pressed is not null && _pressed != sender) return;
        SetHighlight((FanItemView)sender);
    }

    private void OnItemMouseLeave(object sender, MouseEventArgs e)
    {
        if (IsDragging || _pressed == sender) return;
        if (_highlighted == sender) SetHighlight(null);
    }

    private void SetHighlight(FanItemView? view)
    {
        if (_highlighted == view) return;
        var hoverDuration = TimeSpan.FromMilliseconds(110 * Math.Max(0.4, AppSettings.AnimationFactor(_settings.AnimationSpeed)));
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        if (_highlighted is { } old)
        {
            old.ViewModel.IsHighlighted = false;
            var baseZ = _layout?.Items.ElementAtOrDefault(old.SlotIndex).ZIndex ?? 0;
            Panel.SetZIndex(old, baseZ);
            To(old.HoverScale, ScaleTransform.ScaleXProperty, 1, hoverDuration, TimeSpan.Zero, ease);
            To(old.HoverScale, ScaleTransform.ScaleYProperty, 1, hoverDuration, TimeSpan.Zero, ease);
        }
        _highlighted = view;
        if (view is null) return;
        view.ViewModel.IsHighlighted = true;
        Panel.SetZIndex(view, 10_000); // hovered item always above its overlapping neighbours
        To(view.HoverScale, ScaleTransform.ScaleXProperty, HoverScaleFactor, hoverDuration, TimeSpan.Zero, ease);
        To(view.HoverScale, ScaleTransform.ScaleYProperty, HoverScaleFactor, hoverDuration, TimeSpan.Zero, ease);
    }

    // ------------------------------------------------------------------ click & drag

    private void OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsClosing || IsDragging) return;
        var view = (FanItemView)sender;
        _pressed = view;
        _dragStarted = false;
        _pressWindowPoint = e.GetPosition(this);
        _pressItemPoint = e.GetPosition(view);
        SetHighlight(view);
        view.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || _dragStarted || IsDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ReleasePress();
            return;
        }
        var pos = e.GetPosition(this);
        // System drag threshold (SM_CXDRAG/SM_CYDRAG), converted to DIPs.
        var dx = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG)) / _windowScale;
        var dy = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG)) / _windowScale;
        if (Math.Abs(pos.X - _pressWindowPoint.X) < dx && Math.Abs(pos.Y - _pressWindowPoint.Y) < dy)
            return;

        _dragStarted = true;
        var view = _pressed;
        StartDrag(view);
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed is null) return;
        var view = _pressed;
        var wasDrag = _dragStarted;
        ReleasePress();
        if (wasDrag) return;

        // Treat as a click only if released over the same item.
        var hit = InputHitTest(e.GetPosition(this)) as DependencyObject;
        if (IsDescendant(view, hit))
        {
            e.Handled = true;
            OpenRequested?.Invoke(view.ViewModel);
        }
        else if (!view.IsMouseOver && _highlighted == view)
        {
            SetHighlight(null);
        }
    }

    private void OnItemMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsClosing || IsDragging) return;
        var view = (FanItemView)sender;
        SetHighlight(view);
        NativeMethods.GetCursorPos(out var pt);
        e.Handled = true;
        ContextMenuRequested?.Invoke(view.ViewModel, pt);
    }

    private void ReleasePress()
    {
        var p = _pressed;
        _pressed = null;
        _dragStarted = false;
        if (p is not null && p.IsMouseCaptured) p.ReleaseMouseCapture();
    }

    private void StartDrag(FanItemView view)
    {
        var vm = view.ViewModel;
        var path = vm.Path;
        if (string.IsNullOrEmpty(path) || !(System.IO.File.Exists(path) || System.IO.Directory.Exists(path)))
        {
            ReleasePress();
            return;
        }

        // Hand the mouse over to OLE; the fan stays visible and open for the whole operation.
        if (view.IsMouseCaptured) view.ReleaseMouseCapture();
        IsDragging = true;

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dragSize = Math.Min(_settings.ThumbnailSize, 128);
        var image = FileDragSource.CreateDragImage(vm.Thumbnail, dragSize, dpi, vm.IsIcon);
        var offset = new Point(0, 0);
        if (image is not null)
        {
            const double pad = 6;
            var cardPoint = view.PointInCard(_pressItemPoint);
            var contentW = image.PixelWidth / dpi - 2 * pad;
            var contentH = image.PixelHeight / dpi - 2 * pad;
            offset = new Point(
                (pad + cardPoint.X / Math.Max(1, vm.ThumbWidth) * contentW) * dpi,
                (pad + cardPoint.Y / Math.Max(1, vm.ThumbHeight) * contentH) * dpi);
        }

        view.Opacity = 0.55; // "lifted" look while the file is being dragged
        FileDragResult result;
        var effect = 0;
        try
        {
            result = FileDragSource.DoDragDrop(Handle, new[] { path }, image, offset, out effect);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Drag failed: " + ex);
            result = FileDragResult.Failed;
        }
        finally
        {
            IsDragging = false;
            _pressed = null;
            _dragStarted = false;
            view.Opacity = 1;
        }

        DragFinished?.Invoke(vm, result, effect);

        if (_pendingRefresh)
        {
            _pendingRefresh = false;
            Relayout(animate: true);
        }

        if (result == FileDragResult.Dropped && _settings.CloseAfterDrop)
        {
            CloseFan();
        }
        else if (IsOpen)
        {
            // Make sure a later click outside still dismisses the fan.
            Activate();
            if (!view.IsMouseOver && _highlighted == view) SetHighlight(null);
        }
    }

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (node == ancestor) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ------------------------------------------------------------------ wheel, keyboard, dismissal

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsClosing || IsDragging || _pressed is not null) return;
        var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        // Wheel up/away = towards older files (they sit higher in the fan).
        if (_pager.Scroll(e.Delta > 0 ? notches : -notches))
            Relayout(animate: true);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsDragging) return;
        var ordered = _views.OrderBy(v => v.SlotIndex).ToList();
        var index = _highlighted is null ? -1 : ordered.IndexOf(_highlighted);
        switch (e.Key)
        {
            case Key.Escape:
                CloseFan();
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                if (ordered.Count > 0) SetHighlight(ordered[Math.Min(ordered.Count - 1, index + 1)]);
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.Shift:
                if (ordered.Count > 0) SetHighlight(ordered[Math.Max(0, index < 0 ? 0 : index - 1)]);
                e.Handled = true;
                break;
            case Key.Home:
                if (ordered.Count > 0) SetHighlight(ordered[0]);
                e.Handled = true;
                break;
            case Key.End:
                if (ordered.Count > 0) SetHighlight(ordered[^1]);
                e.Handled = true;
                break;
            case Key.PageUp:
                if (_pager.Scroll(Math.Max(1, _pager.Capacity))) Relayout(animate: true);
                e.Handled = true;
                break;
            case Key.PageDown:
                if (_pager.Scroll(-Math.Max(1, _pager.Capacity))) Relayout(animate: true);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                if (_highlighted is not null) OpenRequested?.Invoke(_highlighted.ViewModel);
                e.Handled = true;
                break;
            case Key.Apps:
                if (_highlighted is not null)
                {
                    var p = _highlighted.PointToScreen(new Point(_highlighted.PivotX, _highlighted.ItemHeight / 2));
                    ContextMenuRequested?.Invoke(_highlighted.ViewModel, new POINT((int)p.X, (int)p.Y));
                }
                e.Handled = true;
                break;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Clicking anywhere outside the fan dismisses it – but never during a drag, and not while
        // our own context menu (which briefly takes activation) is showing.
        if (IsDragging || SuppressDeactivation) return;
        CloseFan();
    }

    /// <summary>Set while a context menu owned by the app is open.</summary>
    public bool SuppressDeactivation { get; set; }
}
