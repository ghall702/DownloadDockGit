using DownloadDock.Core.Util;

namespace DownloadDock.Core.Downloads;

/// <summary>Snapshot published by <see cref="DownloadsMonitor"/>.</summary>
public sealed record DownloadsSnapshot(
    string Folder,
    bool FolderExists,
    IReadOnlyList<DownloadEntry> Items,
    int TotalCount,
    DateTime ScannedUtc);

/// <summary>
/// Watches a downloads folder with <see cref="FileSystemWatcher"/>, debounces bursts of
/// change notifications, rescans the newest files and publishes snapshots.
/// Recovers from watcher errors (buffer overflow, network/device loss) and waits for a
/// missing folder to appear.
/// </summary>
public sealed class DownloadsMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Debouncer _debouncer;
    private readonly CompletionChecker _completion;
    private readonly Timer _recoveryTimer;
    private readonly Timer _pendingTimer;
    private FileSystemWatcher? _watcher;
    private string _folder = "";
    private int _maxCount = 50;
    private bool _includeSubfolders;
    private bool _disposed;
    private int _scanRunning;
    private int _scanRequestedWhileRunning;
    private DownloadsSnapshot? _last;

    /// <summary>Raised on a thread-pool thread whenever the visible list may have changed.</summary>
    public event EventHandler<DownloadsSnapshot>? SnapshotChanged;

    /// <summary>Raised when the watcher fails (diagnostics only).</summary>
    public event EventHandler<Exception>? WatcherError;

    public DownloadsSnapshot? Current { get { lock (_gate) return _last; } }

    public TimeSpan DebounceDelay { get; }

    public DownloadsMonitor(TimeSpan? debounce = null, CompletionChecker? completion = null)
    {
        DebounceDelay = debounce ?? TimeSpan.FromMilliseconds(350);
        _debouncer = new Debouncer(RunScan, DebounceDelay, TimeSpan.FromSeconds(3));
        _completion = completion ?? new CompletionChecker();
        _recoveryTimer = new Timer(_ => Recover(), null, Timeout.Infinite, Timeout.Infinite);
        _pendingTimer = new Timer(_ => RequestScan(immediate: true), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>How long to wait before re-checking files that are still being written.</summary>
    public TimeSpan PendingRecheckDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>(Re)configures the monitor and triggers an immediate scan.</summary>
    public void Start(string folder, int maxCount, bool includeSubfolders)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DownloadsMonitor));
            _folder = folder;
            _maxCount = Math.Max(1, maxCount);
            _includeSubfolders = includeSubfolders;
            CreateWatcher();
        }
        RequestScan(immediate: true);
    }

    /// <summary>Requests a rescan (debounced unless <paramref name="immediate"/>).</summary>
    public void RequestScan(bool immediate = false)
    {
        if (immediate)
            ThreadPool.QueueUserWorkItem(_ => RunScan());
        else
            _debouncer.Trigger();
    }

    /// <summary>Synchronous scan – used by tests and at startup.</summary>
    public DownloadsSnapshot ScanNow()
    {
        RunScan();
        return Current!;
    }

    private void CreateWatcher()
    {
        DisposeWatcher();
        if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder))
        {
            // Folder missing (e.g. external drive unplugged) – poll until it reappears.
            _recoveryTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
            return;
        }

        try
        {
            var w = new FileSystemWatcher(_folder)
            {
                IncludeSubdirectories = _includeSubfolders,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                               | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Attributes,
            };
            w.Created += OnFsEvent;
            w.Changed += OnFsEvent;
            w.Deleted += OnFsEvent;
            w.Renamed += OnFsEvent;
            w.Error += OnWatcherError;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            WatcherError?.Invoke(this, ex);
            _recoveryTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => _debouncer.Trigger();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        WatcherError?.Invoke(this, e.GetException());
        // Buffer overflow or lost handle: rebuild the watcher shortly and rescan everything.
        lock (_gate)
        {
            if (!_disposed) _recoveryTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private void Recover()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CreateWatcher();
        }
        RequestScan(immediate: true);
    }

    private void RunScan()
    {
        if (Interlocked.Exchange(ref _scanRunning, 1) == 1)
        {
            // A scan is in progress; make sure another one follows it.
            Interlocked.Exchange(ref _scanRequestedWhileRunning, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _scanRequestedWhileRunning, 0);
                ScanOnce();
            }
            while (Interlocked.Exchange(ref _scanRequestedWhileRunning, 0) == 1);
        }
        finally
        {
            Interlocked.Exchange(ref _scanRunning, 0);
        }
    }

    private void ScanOnce()
    {
        string folder;
        int maxCount;
        bool subfolders;
        lock (_gate)
        {
            if (_disposed) return;
            folder = _folder;
            maxCount = _maxCount;
            subfolders = _includeSubfolders;
        }

        var exists = Directory.Exists(folder);
        // Over-fetch so that pending (still downloading) files don't shrink the list.
        var scan = FolderScanner.Scan(folder, maxCount + 16, subfolders);

        var complete = new List<DownloadEntry>(maxCount);
        var anyPending = false;
        foreach (var entry in scan.Newest)
        {
            if (_completion.Check(entry) == CompletionChecker.State.Pending)
            {
                anyPending = true;
                continue;
            }
            complete.Add(entry);
            if (complete.Count >= maxCount) break;
        }

        var snapshot = new DownloadsSnapshot(folder, exists, complete, scan.TotalVisibleCount, DateTime.UtcNow);
        bool changed;
        lock (_gate)
        {
            changed = _last is null || !SameContent(_last, snapshot);
            _last = snapshot;
        }

        if (anyPending)
        {
            // Check again shortly until the download settles (no FS event is guaranteed when a writer closes).
            lock (_gate)
            {
                if (!_disposed) _pendingTimer.Change(PendingRecheckDelay, Timeout.InfiniteTimeSpan);
            }
        }

        if (changed)
            SnapshotChanged?.Invoke(this, snapshot);
    }

    internal static bool SameContent(DownloadsSnapshot a, DownloadsSnapshot b)
    {
        if (!string.Equals(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.FolderExists != b.FolderExists || a.TotalCount != b.TotalCount) return false;
        if (a.Items.Count != b.Items.Count) return false;
        for (var i = 0; i < a.Items.Count; i++)
        {
            if (a.Items[i].VersionKey != b.Items[i].VersionKey) return false;
        }
        return true;
    }

    private void DisposeWatcher()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnFsEvent;
        _watcher.Changed -= OnFsEvent;
        _watcher.Deleted -= OnFsEvent;
        _watcher.Renamed -= OnFsEvent;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeWatcher();
        }
        _debouncer.Dispose();
        _recoveryTimer.Dispose();
        _pendingTimer.Dispose();
    }
}
