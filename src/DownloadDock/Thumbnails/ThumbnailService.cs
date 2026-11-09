using System.Collections.Concurrent;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Previews;
using DownloadDock.Core.Util;

namespace DownloadDock.Thumbnails;

/// <summary>
/// Asynchronous, cached thumbnail generation. Work runs on two below-normal-priority STA threads,
/// identical requests are de-duplicated, recent results are kept in memory (LRU) and expensive
/// ones on disk. The UI never waits on rendering: items show a placeholder and fade the preview in.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly StaWorkerPool _pool = new(2, "DownloadDock thumbnails");
    private readonly LruCache<string, ThumbnailResult> _memory = new(160, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ThumbnailResult?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiskThumbnailCache _disk;

    public ThumbnailService(string? diskCacheDirectory = null)
    {
        _disk = new DiskThumbnailCache(diskCacheDirectory ?? DiskThumbnailCache.DefaultDirectory);
        _ = Task.Run(_disk.Prune);
    }

    private static string MemoryKey(DownloadEntry entry, int px) => entry.VersionKey + "|" + px;

    /// <summary>Returns a cached thumbnail without doing any work, or null.</summary>
    public ThumbnailResult? TryGetCached(DownloadEntry entry, int px)
        => _memory.TryGet(MemoryKey(entry, px), out var r) ? r : null;

    public Task<ThumbnailResult?> GetAsync(DownloadEntry entry, int px, bool highPriority = true)
    {
        var key = MemoryKey(entry, px);
        if (_memory.TryGet(key, out var cached))
            return Task.FromResult<ThumbnailResult?>(cached);

        return _inflight.GetOrAdd(key, _ =>
        {
            var task = _pool.Run(() => Produce(entry, px), highPriority);
            task.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is { } result)
                    _memory.Set(key, result);
                _inflight.TryRemove(key, out Task<ThumbnailResult?>? _);
            }, TaskScheduler.Default);
            return task;
        });
    }

    /// <summary>Gets the Shell icon for a path (used for the folder item).</summary>
    public Task<System.Windows.Media.Imaging.BitmapSource?> GetIconAsync(string path, int px)
    {
        var key = "icon|" + path + "|" + px;
        if (_memory.TryGet(key, out var cached))
            return Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(cached.Image);
        return _pool.Run(() =>
        {
            var img = ShellThumbnailer.GetIcon(path, px);
            if (img is not null) _memory.Set(key, new ThumbnailResult(img, true, "shell-icon"));
            return img;
        }, highPriority: true);
    }

    /// <summary>Renders thumbnails in the background so the fan opens with previews ready.</summary>
    public void Prefetch(IEnumerable<DownloadEntry> entries, int px)
    {
        foreach (var e in entries)
        {
            if (!_memory.TryGet(MemoryKey(e, px), out _))
                _ = GetAsync(e, px, highPriority: false);
        }
    }

    public void ClearMemory() => _memory.Clear();

    public void ClearAll()
    {
        _memory.Clear();
        _disk.Clear();
    }

    private ThumbnailResult? Produce(DownloadEntry entry, int px)
    {
        var kind = FileKinds.Classify(entry.FullPath);
        var cacheable = FileKinds.IsWorthDiskCaching(kind);
        var diskKey = DiskThumbnailCache.MakeKey(entry.VersionKey, px);
        if (cacheable && _disk.TryLoad(diskKey) is { } fromDisk)
            return new ThumbnailResult(fromDisk, false, "disk-cache");

        var result = ThumbnailRenderer.Render(entry.FullPath, px);
        if (result is { IsIcon: false } && cacheable)
            _disk.Save(diskKey, result.Image);
        return result;
    }

    public void Dispose() => _pool.Dispose();
}
