using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadDock.Interop;

namespace DownloadDock.Screenshots;

/// <summary>A display: bounds and work area in physical pixels, plus its DPI scale.</summary>
internal sealed record MonitorInfo(IntPtr Handle, RECT Bounds, RECT WorkArea, double DpiScale, bool IsPrimary);

/// <summary>
/// Captures screen pixels with GDI BitBlt (CAPTUREBLT includes layered windows). The process is
/// Per-Monitor-V2 DPI aware, so all coordinates are physical pixels across every monitor and scale.
/// </summary>
internal static class ScreenCapture
{
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        ScreenshotNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            list.Add(Describe(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo Describe(IntPtr hMonitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(hMonitor, ref mi);
        var scale = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                scale = dx / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return new MonitorInfo(hMonitor, mi.rcMonitor, mi.rcWork, scale, (mi.dwFlags & 1) != 0);
    }

    public static MonitorInfo MonitorAtCursor()
    {
        NativeMethods.GetCursorPos(out var p);
        return Describe(NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTONEAREST));
    }

    public static RECT VirtualScreen() => new(
        NativeMethods.GetSystemMetrics(ScreenshotNative.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(ScreenshotNative.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(ScreenshotNative.SM_XVIRTUALSCREEN) + NativeMethods.GetSystemMetrics(ScreenshotNative.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(ScreenshotNative.SM_YVIRTUALSCREEN) + NativeMethods.GetSystemMetrics(ScreenshotNative.SM_CYVIRTUALSCREEN));

    /// <summary>Copies a rectangle of the screen (physical pixels) into a frozen, opaque bitmap.</summary>
    public static BitmapSource Capture(RECT r)
    {
        var w = Math.Max(1, r.Width);
        var h = Math.Max(1, r.Height);
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        var mem = ScreenshotNative.CreateCompatibleDC(screen);
        var bih = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };
        var dib = NativeMethods.CreateDIBSection(screen, ref bih, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate a capture bitmap.");
            var old = ScreenshotNative.SelectObject(mem, dib);
            var ok = ScreenshotNative.BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, ScreenshotNative.SRCCOPY | ScreenshotNative.CAPTUREBLT);
            ScreenshotNative.SelectObject(mem, old);
            if (!ok) throw new InvalidOperationException("BitBlt failed (error " + Marshal.GetLastWin32Error() + ").");
            var stride = w * 4;
            // GDI leaves the alpha byte undefined: treat the pixels as opaque BGR32.
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bits, stride * h, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            ScreenshotNative.DeleteDC(mem);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Crops part of a captured bitmap (coordinates relative to that bitmap).</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        rect = Clamp(rect, source.PixelWidth, source.PixelHeight);
        var cropped = new CroppedBitmap(source, rect);
        var copy = new WriteableBitmap(cropped); // detach from the (large) source
        copy.Freeze();
        return copy;
    }

    internal static Int32Rect Clamp(Int32Rect r, int width, int height)
    {
        var x = Math.Clamp(r.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(r.Y, 0, Math.Max(0, height - 1));
        var w = Math.Clamp(r.Width, 1, width - x);
        var h = Math.Clamp(r.Height, 1, height - y);
        return new Int32Rect(x, y, w, h);
    }
}
