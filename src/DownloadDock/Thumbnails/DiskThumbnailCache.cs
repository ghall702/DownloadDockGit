using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace DownloadDock.Thumbnails;

/// <summary>
/// Persists expensive previews (PDF pages, Office renders, video frames) as PNG files under
/// %LOCALAPPDATA%\DownloadDock\ThumbCache so they are instant after a restart. Keys include the
/// file's size and write time, so edited files are re-rendered automatically.
/// </summary>
internal sealed class DiskThumbnailCache
{
    /// <summary>Bump when rendering changes so stale cached images are ignored.</summary>
    public const int RendererVersion = 3;
    private const int MaxFiles = 600;

    private readonly string _directory;

    public DiskThumbnailCache(string directory)
    {
        _directory = directory;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadDock", "ThumbCache");

    public static string MakeKey(string versionKey, int pixels)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{RendererVersion}|{pixels}|{versionKey.ToUpperInvariant()}"));
        return Convert.ToHexString(bytes, 0, 20);
    }

    public BitmapSource? TryLoad(string key)
    {
        var path = Path.Combine(_directory, key + ".png");
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            TryDelete(path);
            return null;
        }
    }

    public void Save(string key, BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, key + ".png");
            var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = File.Create(tmp))
                encoder.Save(fs);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Removes the least recently used files beyond the size limit.</summary>
    public void Prune()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            var files = new DirectoryInfo(_directory).GetFiles("*.png");
            if (files.Length <= MaxFiles) return;
            foreach (var f in files.OrderByDescending(f => f.LastAccessTimeUtc).Skip(MaxFiles * 3 / 4))
                TryDelete(f.FullName);
            foreach (var tmp in new DirectoryInfo(_directory).GetFiles("*.tmp"))
                TryDelete(tmp.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Clear()
    {
        try
        {
            if (Directory.Exists(_directory))
                foreach (var f in Directory.GetFiles(_directory)) TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
