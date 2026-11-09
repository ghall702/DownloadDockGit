using System.Runtime.InteropServices;
using System.Windows.Threading;
using DownloadDock.Interop;
using DownloadDock.Services;

namespace DownloadDock.Dragging;

/// <summary>
/// Notices when *any* app's drag (button held, pointer moving) reaches DownloadDock's tray icon, so a
/// drop catcher can be put over the icon just in time. Windows doesn't tell other processes about OLE
/// drags, and the notification area can't be a drop target, so this watches the mouse with a
/// low-level hook on its own thread (the callback only compares a few numbers and returns).
/// Nothing is consumed or altered – every mouse event passes straight through.
/// </summary>
internal sealed class ForeignDragWatcher : IDisposable
{
    /// <summary>Pointer must move this far from where the button went down before it counts as a drag.</summary>
    private const int DragThreshold = 8;
    /// <summary>Extra margin around the icon so the catcher is ready a little before the pointer arrives.</summary>
    private const int ApproachMargin = 24;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private ScreenshotNative.LowLevelKeyboardProc? _proc;
    private IntPtr _hook;
    private int _threadId;
    private volatile bool _disposed;

    // Hook-thread state.
    private bool _buttonDown;
    private bool _pressedOnTarget;
    private bool _signalled;
    private POINT _pressPoint;

    // Target rectangle (physical pixels), set from the UI thread. Packed into one long pair for atomicity.
    private long _targetLeftTop;
    private long _targetRightBottom;
    private volatile bool _hasTarget;

    /// <summary>The left button went down somewhere (UI thread) – a good moment to refresh the target rectangle.</summary>
    public event Action? ButtonPressed;

    /// <summary>A drag gesture (from anywhere) has reached the target (UI thread). Raised once per press.</summary>
    public event Action? DragApproachingTarget;

    /// <summary>The button was released (UI thread) – any drag is over.</summary>
    public event Action? ButtonReleased;

    public bool IsHooked => _hook != IntPtr.Zero;

    public ForeignDragWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "DownloadDock mouse watcher", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Sets (or clears) the screen rectangle to watch, in physical pixels.</summary>
    public void SetTarget(RECT? rect)
    {
        if (rect is not { } r)
        {
            _hasTarget = false;
            return;
        }
        Interlocked.Exchange(ref _targetLeftTop, ((long)r.Left << 32) | (uint)r.Top);
        Interlocked.Exchange(ref _targetRightBottom, ((long)r.Right << 32) | (uint)r.Bottom);
        _hasTarget = true;
    }

    private bool InTarget(POINT p, int margin)
    {
        if (!_hasTarget) return false;
        var lt = Interlocked.Read(ref _targetLeftTop);
        var rb = Interlocked.Read(ref _targetRightBottom);
        int l = (int)(lt >> 32), t = (int)lt, r = (int)(rb >> 32), b = (int)rb;
        return p.X >= l - margin && p.X < r + margin && p.Y >= t - margin && p.Y < b + margin;
    }

    private void Run()
    {
        _threadId = ScreenshotNative.GetCurrentThreadId();
        _proc = HookProc;
        _hook = ScreenshotNative.SetMouseHook(ScreenshotNative.WH_MOUSE_LL, _proc, ScreenshotNative.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) Log.Error("Mouse hook could not be installed (error " + Marshal.GetLastWin32Error() + ")");
        _ready.Set();
        while (ScreenshotNative.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }
        if (_hook != IntPtr.Zero) ScreenshotNative.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed)
        {
            var msg = (int)wParam;
            switch (msg)
            {
                case ScreenshotNative.WM_LBUTTONDOWN:
                {
                    var info = Marshal.PtrToStructure<ScreenshotNative.MSLLHOOKSTRUCT>(lParam);
                    _buttonDown = true;
                    _signalled = false;
                    _pressPoint = info.pt;
                    // A press on the icon itself is a click or an icon rearrangement – never ours to catch.
                    _pressedOnTarget = InTarget(info.pt, 2);
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, () => ButtonPressed?.Invoke());
                    break;
                }
                case ScreenshotNative.WM_MOUSEMOVE when _buttonDown && !_signalled && !_pressedOnTarget:
                {
                    var info = Marshal.PtrToStructure<ScreenshotNative.MSLLHOOKSTRUCT>(lParam);
                    if (Math.Abs(info.pt.X - _pressPoint.X) + Math.Abs(info.pt.Y - _pressPoint.Y) > DragThreshold
                        && InTarget(info.pt, ApproachMargin))
                    {
                        _signalled = true;
                        _dispatcher.BeginInvoke(DispatcherPriority.Input, () => DragApproachingTarget?.Invoke());
                    }
                    break;
                }
                case ScreenshotNative.WM_LBUTTONUP:
                    if (_buttonDown)
                    {
                        _buttonDown = false;
                        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => ButtonReleased?.Invoke());
                    }
                    break;
            }
        }
        return ScreenshotNative.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) ScreenshotNative.PostThreadMessageW(_threadId, ScreenshotNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }
}
