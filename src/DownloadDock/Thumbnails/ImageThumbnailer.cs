using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DownloadDock.Thumbnails;

/// <summary>Decodes images through WIC at reduced size (fast DCT scaling for JPEG) and applies EXIF orientation.</summary>
internal static class ImageThumbnailer
{
    public static BitmapSource? Render(string path, int maxPixels)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        return Render(fs, maxPixels);
    }

    public static BitmapSource? Render(Stream stream, int maxPixels)
    {
        var start = stream.CanSeek ? stream.Position : 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).First();
        var w = frame.PixelWidth;
        var h = frame.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        var orientation = ReadOrientation(frame.Metadata as BitmapMetadata);

        BitmapSource result;
        if (decoder.Frames.Count == 1 && stream.CanSeek)
        {
            stream.Position = start;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = stream;
            if (w >= h) bi.DecodePixelWidth = Math.Min(maxPixels, w);
            else bi.DecodePixelHeight = Math.Min(maxPixels, h);
            bi.EndInit();
            result = bi;
        }
        else
        {
            // Multi-frame containers (ICO, TIFF): scale the largest frame.
            var scale = Math.Min(1.0, (double)maxPixels / Math.Max(w, h));
            result = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            result = new WriteableBitmap(result);
        }

        result = ApplyOrientation(result, orientation);
        if (result.CanFreeze) result.Freeze();
        return result;
    }

    internal static ushort ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;
        foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                {
                    var o = Convert.ToUInt16(value);
                    if (o is >= 1 and <= 8) return o;
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or FormatException or InvalidCastException or OverflowException or System.Runtime.InteropServices.COMException)
            {
            }
        }
        return 1;
    }

    internal static BitmapSource ApplyOrientation(BitmapSource source, ushort orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (t is null) return source;
        var transformed = new TransformedBitmap(source, t);
        return new WriteableBitmap(transformed); // materialise so the transform isn't re-run on every render
    }
}
