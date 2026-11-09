using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DownloadDock.Windows.Tests;

/// <summary>Runs code on a fresh STA thread (WPF, WIC, Shell and OLE all require STA).</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> func, int timeoutMs = 60_000)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(timeoutMs)) throw new TimeoutException("STA work timed out");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    public static void Run(Action action, int timeoutMs = 60_000) => Run(() => { action(); return 0; }, timeoutMs);
}

internal static class Pixels
{
    /// <summary>Reads one pixel as (B, G, R, A).</summary>
    public static (byte B, byte G, byte R, byte A) At(BitmapSource bmp, int x, int y)
    {
        var src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var buf = new byte[4];
        src.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), buf, 4, 0);
        return (buf[0], buf[1], buf[2], buf[3]);
    }

    /// <summary>Number of distinct colours (rough content check – a blank image has 1).</summary>
    public static int DistinctColours(BitmapSource bmp)
    {
        var src = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var stride = src.PixelWidth * 4;
        var data = new byte[stride * src.PixelHeight];
        src.CopyPixels(data, stride, 0);
        var set = new HashSet<int>();
        for (var i = 0; i < data.Length; i += 4) set.Add(BitConverter.ToInt32(data, i));
        return set.Count;
    }
}
