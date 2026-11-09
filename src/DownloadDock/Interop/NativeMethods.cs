using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace DownloadDock.Interop;

#pragma warning disable IDE1006, CA1069 // Win32 naming

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"{Left},{Top},{Right},{Bottom}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
    public POINT(int x, int y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx, cy;
    public SIZE(int x, int y) { cx = x; cy = y; }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATAW
{
    public int cbSize;
    public IntPtr hWnd;
    public int uID;
    public int uFlags;
    public int uCallbackMessage;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    public int dwState;
    public int dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
    public int uVersion; // union with uTimeout
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
    public int dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NOTIFYICONIDENTIFIER
{
    public int cbSize;
    public IntPtr hWnd;
    public int uID;
    public Guid guidItem;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public int dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public int uCallbackMessage;
    public int uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public int biSize;
    public int biWidth;
    public int biHeight;
    public short biPlanes;
    public short biBitCount;
    public int biCompression;
    public int biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public int biClrUsed;
    public int biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public short bmPlanes;
    public short bmBitsPixel;
    public IntPtr bmBits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
    public int xHotspot;
    public int yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SHDRAGIMAGE
{
    public SIZE sizeDragImage;
    public POINT ptOffset;
    public IntPtr hbmpDragImage;
    public int crColorKey;
}

internal static class NativeMethods
{
    // ---------------------------------------------------------------- messages / constants
    public const int WM_NULL = 0x0000;
    public const int WM_DESTROY = 0x0002;
    public const int WM_ACTIVATE = 0x0006;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_COMMAND = 0x0111;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_APP = 0x8000;
    public const int WM_USER = 0x0400;

    public const int NIN_SELECT = WM_USER + 0;
    public const int NIN_KEYSELECT = WM_USER + 1;
    public const int NIN_POPUPOPEN = WM_USER + 6;
    public const int NIN_POPUPCLOSE = WM_USER + 7;

    public const int NIM_ADD = 0x0;
    public const int NIM_MODIFY = 0x1;
    public const int NIM_DELETE = 0x2;
    public const int NIM_SETFOCUS = 0x3;
    public const int NIM_SETVERSION = 0x4;
    public const int NIF_MESSAGE = 0x1;
    public const int NIF_ICON = 0x2;
    public const int NIF_TIP = 0x4;
    public const int NIF_SHOWTIP = 0x80;
    public const int NOTIFYICON_VERSION_4 = 4;

    public const int MF_STRING = 0x0;
    public const int MF_SEPARATOR = 0x800;
    public const int MF_GRAYED = 0x1;
    public const int MF_CHECKED = 0x8;
    public const int MF_DEFAULT = 0x1000;
    public const int TPM_RIGHTBUTTON = 0x0002;
    public const int TPM_RETURNCMD = 0x0100;
    public const int TPM_NONOTIFY = 0x0080;
    public const int TPM_BOTTOMALIGN = 0x0020;
    public const int TPM_RIGHTALIGN = 0x0008;
    public const int TPM_VERNEGANIMATION = 0x2000;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_APPWINDOW = 0x00040000;

    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOMOVE = 0x0002;
    public const int SWP_NOZORDER = 0x0004;
    public const int SWP_NOACTIVATE = 0x0010;
    public const int SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    public const int ABM_GETTASKBARPOS = 0x5;
    public const int ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    public const int DIB_RGB_COLORS = 0;
    public const int BI_RGB = 0;

    public const int DROPEFFECT_NONE = 0;
    public const int DROPEFFECT_COPY = 1;
    public const int DROPEFFECT_MOVE = 2;
    public const int DROPEFFECT_LINK = 4;
    public const int DRAGDROP_S_DROP = 0x00040100;
    public const int DRAGDROP_S_CANCEL = 0x00040101;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_ROUND = 2;
    public const int DWMSBT_MAINWINDOW = 2; // Mica

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    // ---------------------------------------------------------------- shell32
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll")]
    public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("shell32.dll")]
    public static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHCreateShellItemArrayFromIDLists(
        uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgpidl,
        out IShellItemArray ppsiItemArray);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHDoDragDrop(IntPtr hwnd, ComTypes.IDataObject pdata, IntPtr pdsrc, int dwEffect, out int pdwEffect);

    [DllImport("shell32.dll", EntryPoint = "SHDoDragDrop", PreserveSig = true)]
    public static extern int SHDoDragDropWithSource(IntPtr hwnd, ComTypes.IDataObject pdata, IntPtr pdsrc, int dwEffect, out int pdwEffect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int DragQueryFileW(IntPtr hDrop, uint iFile, [Out] char[]? lpszFile, int cch);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM pmedium);

    // ---------------------------------------------------------------- user32
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(IntPtr hMenu, int uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hmenu, int fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int nIndex, int dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    public static extern int GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // ---------------------------------------------------------------- shcore
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------------------------------------------------------------- gdi32
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, int usage, out IntPtr ppvBits, IntPtr hSection, int offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateBitmap(int nWidth, int nHeight, int nPlanes, int nBitCount, IntPtr lpBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetObjectW(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, int start, int cLines, IntPtr lpvBits, ref BITMAPINFOHEADER lpbmi, int usage);

    // ---------------------------------------------------------------- kernel32
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    // ---------------------------------------------------------------- dwmapi
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ---------------------------------------------------------------- uxtheme (undocumented, ordinal exports used by Explorer for dark menus)
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    public static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = false)]
    public static extern void FlushMenuThemes();

    public static int LoWord(IntPtr value) => unchecked((short)(long)value);
    public static int HiWord(IntPtr value) => unchecked((short)((long)value >> 16));
}
