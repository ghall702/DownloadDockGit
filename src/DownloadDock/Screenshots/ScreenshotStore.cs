using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using DownloadDock.Core.Screenshots;
using DownloadDock.Core.Settings;
using DownloadDock.Interop;
using DownloadDock.Services;

namespace DownloadDock.Screenshots;

/// <summary>One captured screenshot: the in-memory image plus the PNG file it is (being) written to.</summary>
internal sealed class ScreenshotItem
{
    public ScreenshotItem(BitmapSource image, Task<string> fileTask, bool isTemporary, string source, IntPtr monitor)
    {
        Image = image;
        FileTask = fileTask;
        IsTemporary = isTemporary;
        Source = source;
        Monitor = monitor;
    }

    public BitmapSource Image { get; }

    /// <summary>Completes with the PNG path once it is fully written.</summary>
    public Task<string> FileTask { get; private set; }

    public bool IsTemporary { get; private set; }

    /// <summary>"fullscreen", "region", "snippingtool-file", "clipboard" … (diagnostics).</summary>
    public string Source { get; }

    /// <summary>Monitor the thumbnail should appear on.</summary>
    public IntPtr Monitor { get; }

    /// <summary>Waits (briefly) for the file; used right before a drag needs a real path.</summary>
    public string? WaitForFile(TimeSpan timeout)
    {
        try
        {
            return FileTask.Wait(timeout) ? FileTask.Result : null;
        }
        catch (AggregateException ex)
        {
            Log.Error("Screenshot file was not written", ex.InnerException);
            return null;
        }
    }

    public void Replace(Task<string> fileTask, bool isTemporary)
    {
        FileTask = fileTask;
        IsTemporary = isTemporary;
    }
}

/// <summary>
/// Writes screenshots as PNG files. Permanent mode saves straight into the screenshot folder (default
/// Pictures\Screenshots); temporary mode keeps them under %LOCALAPPDATA%\DownloadDock\Screenshots until
/// you choose Save. Temporary files are only removed after a day (so any app you dropped one into has long
/// finished reading it) and never while they are being dragged.
/// </summary>
internal sealed class ScreenshotStore
{
    private static readonly Guid FOLDERID_Screenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    private readonly ConcurrentDictionary<string, int> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _ownFiles = new(StringComparer.OrdinalIgnoreCase);

    public ScreenshotStore(Func<AppSettings> settings)
    {
        Settings = settings;
    }

    public Func<AppSettings> Settings { get; }

    public static string TemporaryFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadDock", "Screenshots");

    /// <summary>The Windows "Screenshots" known folder (where Snipping Tool and Win+PrtScn save).</summary>
    public static string WindowsScreenshotsFolder()
    {
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(FOLDERID_Screenshots, 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    NativeMethods.CoTaskMemFree(p);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    public string PermanentFolder => Settings().ScreenshotFolder ?? WindowsScreenshotsFolder();

    /// <summary>Starts writing the PNG on a background thread and returns immediately.</summary>
    public ScreenshotItem Save(BitmapSource image, string source, IntPtr monitor)
    {
        var temporary = Settings().ScreenshotSaveMode == ScreenshotSaveMode.Temporary;
        var folder = temporary ? TemporaryFolder : PermanentFolder;
        var time = DateTime.Now;
        var task = Task.Run(() => WritePng(image, folder, time));
        return new ScreenshotItem(image, task, temporary, source, monitor);
    }

    /// <summary>Wraps a screenshot Windows already saved (Snipping Tool / Win+PrtScn): nothing is copied.</summary>
    public static ScreenshotItem FromExistingFile(string path, BitmapSource image, string source, IntPtr monitor)
        => new(image, Task.FromResult(path), isTemporary: false, source, monitor);

    /// <summary>Makes a temporary screenshot permanent (moves it into the screenshot folder).</summary>
    public string MakePermanent(ScreenshotItem item)
    {
        var current = item.WaitForFile(TimeSpan.FromSeconds(10)) ?? throw new IOException("The screenshot file is not available.");
        if (!item.IsTemporary) return current;
        var folder = PermanentFolder;
        Directory.CreateDirectory(folder);
        var target = ScreenshotNaming.UniquePath(folder, File.GetCreationTime(current));
        MarkOwn(target);
        // Copy rather than move: an app the temp file was dropped into may still be reading it.
        File.Copy(current, target);
        item.Replace(Task.FromResult(target), isTemporary: false);
        return target;
    }

    public string WritePng(BitmapSource image, string folder, DateTime localTime)
    {
        Directory.CreateDirectory(folder);
        var path = ScreenshotNaming.UniquePath(folder, localTime);
        MarkOwn(path);
        var tmp = path + ".partial";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            encoder.Save(fs);
        File.Move(tmp, path, overwrite: false);
        return path;
    }

    /// <summary>Remembers files DownloadDock wrote so the screenshot-folder watcher doesn't show them twice.</summary>
    private void MarkOwn(string path) => _ownFiles[path] = DateTime.UtcNow;

    public bool IsOwnFile(string path) => _ownFiles.ContainsKey(path) || _ownFiles.ContainsKey(path.Replace(".partial", ""));

    /// <summary>Marks a file as being dragged so it can never be cleaned up underneath the drop target.</summary>
    public IDisposable Use(string path)
    {
        _inUse.AddOrUpdate(path, 1, (_, n) => n + 1);
        return new Release(() => _inUse.AddOrUpdate(path, 0, (_, n) => Math.Max(0, n - 1)));
    }

    public bool IsInUse(string path) => _inUse.TryGetValue(path, out var n) && n > 0;

    /// <summary>Deletes temporary screenshots older than a day that are not in use.</summary>
    public void CleanTemporaryFiles()
    {
        try
        {
            if (!Directory.Exists(TemporaryFolder)) return;
            var files = new DirectoryInfo(TemporaryFolder).GetFiles().Select(f => (f.FullName, f.LastWriteTimeUtc));
            var inUse = new HashSet<string>(_inUse.Where(kv => kv.Value > 0).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var path in TemporaryScreenshotPolicy.FilesToDelete(files, DateTime.UtcNow, inUse))
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Temporary screenshot cleanup failed", ex);
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }
}
