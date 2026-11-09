using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DownloadDock.Interop;

/// <summary>Conversions between WPF bitmaps and GDI HBITMAP / HICON handles, preserving alpha.</summary>
internal static class BitmapInterop
{
    /// <summary>Creates a top-down 32bpp premultiplied DIB section (caller owns the handle).</summary>
    public static IntPtr CreatePremultipliedHBitmap(BitmapSource source)
    {
        var bmp = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        return CreateDib(bmp);
    }

    /// <summary>Creates a 32bpp DIB with straight (non-premultiplied) alpha, as icons require.</summary>
    public static IntPtr CreateStraightAlphaHBitmap(BitmapSource source)
    {
        var bmp = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        return CreateDib(bmp);
    }

    private static IntPtr CreateDib(BitmapSource bmp)
    {
        var w = bmp.PixelWidth;
        var h = bmp.PixelHeight;
        var stride = w * 4;
        var bih = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };
        var hbmp = NativeMethods.CreateDIBSection(IntPtr.Zero, ref bih, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
        bmp.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), bits, stride * h, stride);
        return hbmp;
    }

    /// <summary>
    /// Converts a shell HBITMAP (32bpp, usually premultiplied alpha) to a frozen BitmapSource.
    /// Does not delete the handle.
    /// </summary>
    public static BitmapSource? FromHBitmap(IntPtr hbmp)
    {
        if (hbmp == IntPtr.Zero) return null;
        if (NativeMethods.GetObjectW(hbmp, Marshal.SizeOf<BITMAP>(), out var info) == 0) return null;
        var w = info.bmWidth;
        var h = Math.Abs(info.bmHeight);
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

        var bih = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // request top-down rows regardless of the source orientation
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };
        var stride = w * 4;
        var pixels = new byte[stride * h];
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    if (NativeMethods.GetDIBits(hdc, hbmp, 0, h, (IntPtr)p, ref bih, NativeMethods.DIB_RGB_COLORS) == 0)
                        return null;
                }
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }

        // Bitmaps without an alpha channel come back with alpha = 0 everywhere: make them opaque.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Creates an HICON from a bitmap (caller must DestroyIcon).</summary>
    public static IntPtr CreateHIcon(BitmapSource source)
    {
        var color = CreateStraightAlphaHBitmap(source);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        var mask = NativeMethods.CreateBitmap(source.PixelWidth, source.PixelHeight, 1, 1, IntPtr.Zero);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return NativeMethods.CreateIconIndirect(ref info);
        }
        finally
        {
            NativeMethods.DeleteObject(color);
            NativeMethods.DeleteObject(mask);
        }
    }
}
