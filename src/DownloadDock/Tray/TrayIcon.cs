using System.Runtime.InteropServices;
using System.Windows.Interop;
using DownloadDock.Interop;
using DownloadDock.Services;

namespace DownloadDock.Tray;

/// <summary>
/// A native notification-area icon (Shell_NotifyIcon, NOTIFYICON_VERSION_4) owned by a hidden
/// window. Survives Explorer restarts (TaskbarCreated), follows taskbar theme and DPI changes, and
/// can report its exact on-screen rectangle via Shell_NotifyIconGetRect for anchoring the fan.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = NativeMethods.WM_APP + 1;

    private readonly HwndSource _window;
    private readonly int _taskbarCreatedMessage;
    private readonly string _tooltip;
    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;

    /// <summary>Left click or keyboard activation. Argument: anchor point (physical pixels).</summary>
    public event Action<POINT>? Invoked;

    /// <summary>Right click / context-menu key. Argument: menu point (physical pixels).</summary>
    public event Action<POINT>? ContextMenuRequested;

    /// <summary>Windows theme (light/dark) or accent colour changed.</summary>
    public event Action? ThemeChanged;

    /// <summary>Display configuration changed (monitors, DPI, taskbar).</summary>
    public event Action? DisplayChanged;

    public IntPtr Handle => _window.Handle;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        var p = new HwndSourceParameters("DownloadDockTrayWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
        };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
    }

    public void Show()
    {
        UpdateIconImage();
        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
        if (!_added)
        {
            // An icon with our id may survive a crash; modify instead.
            _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        }
        data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
    }

    /// <summary>Returns the icon's screen rectangle (physical pixels), or null when Windows can't tell (e.g. hidden in overflow).</summary>
    public RECT? GetIconRect()
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window.Handle,
            uID = IconId,
        };
        if (NativeMethods.Shell_NotifyIconGetRect(ref id, out var rect) == 0 && rect.Width > 0 && rect.Height > 0)
            return rect;
        return null;
    }

    /// <summary>Returns keyboard focus to the notification area (after the fan closes via keyboard).</summary>
    public void ReturnFocus()
    {
        var data = CreateData(0);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETFOCUS, ref data);
    }

    public void UpdateIconImage()
    {
        var dpi = NativeMethods.GetDpiForWindow(_window.Handle);
        if (dpi <= 0) dpi = 96;
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        if (size <= 0) size = 16 * dpi / 96;
        var bmp = TrayIconRenderer.Render(size, ThemeService.SystemUsesDarkTheme());
        var newIcon = BitmapInterop.CreateHIcon(bmp);
        if (newIcon == IntPtr.Zero) return;

        var old = _hIcon;
        _hIcon = newIcon;
        if (_added)
        {
            var data = CreateData(NativeMethods.NIF_ICON);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        }
        if (old != IntPtr.Zero) NativeMethods.DestroyIcon(old);
    }

    private NOTIFYICONDATAW CreateData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var evt = NativeMethods.LoWord(lParam);
            var anchor = new POINT(NativeMethods.LoWord(wParam), NativeMethods.HiWord(wParam));
            switch (evt)
            {
                case NativeMethods.NIN_SELECT:
                case NativeMethods.NIN_KEYSELECT:
                    Invoked?.Invoke(anchor);
                    break;
                case NativeMethods.WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(anchor);
                    break;
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted: our icon is gone, add it again.
            _added = false;
            Show();
            DisplayChanged?.Invoke();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case NativeMethods.WM_SETTINGCHANGE:
                var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                if (area == "ImmersiveColorSet")
                {
                    UpdateIconImage();
                    ThemeChanged?.Invoke();
                }
                else if (area is null || area == "TraySettings")
                {
                    DisplayChanged?.Invoke();
                }
                break;
            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
                UpdateIconImage();
                DisplayChanged?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = CreateData(0);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
        }
        if (_hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(_hIcon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
