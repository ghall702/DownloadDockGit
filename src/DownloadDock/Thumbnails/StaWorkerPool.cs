namespace DownloadDock.Thumbnails;

/// <summary>
/// A small pool of below-normal-priority STA threads. Shell thumbnail providers, WIC and WPF
/// rendering (RenderTargetBitmap) all require STA, and running them here keeps the UI thread free.
/// High-priority work (items currently on screen) jumps ahead of prefetch work.
/// </summary>
internal sealed class StaWorkerPool : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<Action> _high = new();
    private readonly LinkedList<Action> _low = new();
    private readonly List<Thread> _threads = new();
    private bool _disposed;

    public StaWorkerPool(int threadCount, string name)
    {
        for (var i = 0; i < threadCount; i++)
        {
            var t = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"{name} #{i + 1}",
            };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _threads.Add(t);
        }
    }

    public Task<T> Run<T>(Func<T> work, bool highPriority)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Execute()
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        lock (_gate)
        {
            if (_disposed)
            {
                tcs.TrySetCanceled();
                return tcs.Task;
            }
            if (highPriority) _high.AddLast(Execute); else _low.AddLast(Execute);
            Monitor.Pulse(_gate);
        }
        return tcs.Task;
    }

    private void Loop()
    {
        while (true)
        {
            Action job;
            lock (_gate)
            {
                while (!_disposed && _high.Count == 0 && _low.Count == 0)
                    Monitor.Wait(_gate);
                if (_disposed) return;
                var list = _high.Count > 0 ? _high : _low;
                job = list.First!.Value;
                list.RemoveFirst();
            }
            job();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
