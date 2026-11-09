using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using DownloadDock.Interop;

namespace DownloadDock.Thumbnails;

/// <summary>
/// Asks Windows Shell (IShellItemImageFactory) for a thumbnail. This uses whatever thumbnail
/// providers are installed – built-in ones for images and video, Office's for documents when
/// Office is installed, third-party ones – and the system icon as the final fallback.
/// </summary>
internal static class ShellThumbnailer
{
    public static BitmapSource? GetThumbnail(string path, int px) => Get(path, px, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk);

    public static BitmapSource? GetIcon(string path, int px) => Get(path, px, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk);

    private static BitmapSource? Get(string path, int px, SIIGBF flags)
    {
        object? obj = null;
        var hbmp = IntPtr.Zero;
        try
        {
            if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ShellGuids.IID_IShellItemImageFactory, out obj) != 0 || obj is not IShellItemImageFactory factory)
                return null;
            if (factory.GetImage(new SIZE(px, px), flags, out hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            return BitmapInterop.FromHBitmap(hbmp);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) NativeMethods.DeleteObject(hbmp);
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }
}
