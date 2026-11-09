namespace DownloadDock.Services;

/// <summary>
/// Ensures one instance per user session. A second launch (e.g. from the Start menu) signals the
/// running instance to open its fan and then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly bool _owned;
    private RegisteredWaitHandle? _registration;

    public SingleInstance(string id)
    {
        var name = $"Local\\{id}-{Environment.UserName}";
        _mutex = new Mutex(true, name + "-mutex", out _owned);
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
    }

    public bool IsFirstInstance => _owned;

    public void SignalFirstInstance() => _signal.Set();

    public void ListenForSignals(Action onSignal)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _signal.Dispose();
    }
}
