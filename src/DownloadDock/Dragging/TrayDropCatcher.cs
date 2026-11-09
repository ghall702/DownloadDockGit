using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DownloadDock.Interop;
using DownloadDock.Services;

namespace DownloadDock.Dragging;

/// <summary>
/// A tiny, almost invisible OLE drop target laid exactly over DownloadDock's tray icon while a drag is
/// in progress (the notification area itself belongs to Explorer and can't accept drops). With files
/// over it, it shows a highlight; resting there springs the fan open, and dropping right on the icon
/// copies the files into Downloads. It never takes focus and is hidden again as soon as the drag ends.
/// </summary>
internal sealed class TrayDropCatcher : Window
{
    private readonly Border _highlight;
    private readonly DispatcherTimer _dwell;
    private string[]? _hoverFiles;

    /// <summary>Files are resting on the icon – open the fan so they can be dropped there.</summary>
    public event Action? SpringRequested;

    /// <summary>Files were dropped directly onto the icon.</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>Decides whether a set of dragged files is acceptable (e.g. not already in Downloads).</summary>
    public Func<string[], bool> Accepts { get; set; } = _ => true;

    public TimeSpan DwellTime { get; set; } = TimeSpan.FromMilliseconds(400);

    public bool IsArmed => IsVisible;

    public TrayDropCatcher()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Title = "DownloadDock drop target";
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        // Alpha 1/255: invisible, but layered-window hit-testing (and so OLE drag-and-drop) sees it.
        Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        AllowDrop = true;

        var accent = ThemeService.AccentColor();
        _highlight = new Border
        {
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(accent),
            Background = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Content = new Grid { Children = { _highlight } };

        _dwell = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = DwellTime };
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            if (_hoverFiles is not null)
            {
                Log.Info("Files resting on the tray icon: springing the fan open");
                SpringRequested?.Invoke();
            }
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)(ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
            ScreenshotNative.SetWindowDisplayAffinity(hwnd, ScreenshotNative.WDA_EXCLUDEFROMCAPTURE);
        };

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => Unhover();
        Drop += OnDrop;
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Covers the given screen rectangle (physical pixels) without activating.</summary>
    public void Arm(RECT iconRect)
    {
        var hwnd = Handle;
        Show();
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, iconRect.Left, iconRect.Top, iconRect.Width, iconRect.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    public void Disarm()
    {
        Unhover();
        if (IsVisible) Hide();
    }

    private static string[]? Files(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = Files(e);
        if (files is not { Length: > 0 } || (e.AllowedEffects & DragDropEffects.Copy) == 0 || !Accepts(files))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        if (_hoverFiles is null)
        {
            _hoverFiles = files;
            _highlight.Visibility = Visibility.Visible;
            _dwell.Interval = DwellTime;
            _dwell.Start();
        }
    }

    private void Unhover()
    {
        _dwell.Stop();
        _hoverFiles = null;
        _highlight.Visibility = Visibility.Collapsed;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = Files(e);
        Unhover();
        if (files is not { Length: > 0 } || !Accepts(files))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        Log.Info($"{files.Length} file(s) dropped directly on the tray icon");
        FilesDropped?.Invoke(files);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Disarm);
    }
}
