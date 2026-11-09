namespace DownloadDock.Integration;

public enum IntegrationConnectionState
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,
    /// <summary>The other application isn't running (no server) – retrying with backoff.</summary>
    Unavailable,
    Connecting,
    Connected,
    /// <summary>The other application runs an incompatible protocol major version – retried rarely.</summary>
    Incompatible,
    /// <summary>The pipe exists but access was refused (different owner) – treated as unavailable.</summary>
    AccessDenied,
}

/// <summary>
/// Keeps a connection to the other application alive: connects in the background, reconnects with
/// exponential backoff after it closes or restarts, re-subscribes to events after every reconnect, and
/// can be nudged to retry immediately. Either application may start first or exit at any time.
/// </summary>
public sealed class ResilientIntegrationClient : IAsyncDisposable
{
    private readonly IntegrationClientOptions _options;
    private readonly IReadOnlyList<string> _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private IntegrationClient? _client;
    private Task? _loop;
    private IntegrationConnectionState _state = IntegrationConnectionState.Stopped;

    public ResilientIntegrationClient(IntegrationClientOptions options, IEnumerable<string>? subscribeTo = null)
    {
        _options = options;
        _events = subscribeTo?.ToList() ?? new List<string>();
    }

    public TimeSpan MinRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IncompatibleRetryDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Raised (background thread) whenever <see cref="State"/> changes.</summary>
    public event Action<IntegrationConnectionState>? StateChanged;

    /// <summary>Events from the server (background thread, in order).</summary>
    public event Action<IntegrationEvent>? EventReceived;

    public IntegrationConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>The connected server's hello (version, capabilities), or null.</summary>
    public HelloResult? Server => _client?.IsConnected == true ? _client.Server : null;

    /// <summary>The protocol version reported by an incompatible server, for display.</summary>
    public string? IncompatibleVersion { get; private set; }

    public bool IsConnected => _client?.IsConnected == true;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>Retry now (e.g. the other app just announced itself).</summary>
    public void Nudge()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public bool Supports(string capability) => Server?.Capabilities.Contains(capability) == true;

    /// <summary>Sends a request on the current connection; throws unavailable when not connected.</summary>
    public async Task<TResult?> SendAsync<TResult>(string command, object? payload = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var client = _client;
        if (client is null || !client.IsConnected)
            throw new IntegrationException(IntegrationErrorCodes.Unavailable, "The other application is not connected.");
        return await client.SendAsync<TResult>(command, payload, timeout, ct).ConfigureAwait(false);
    }

    private void SetState(IntegrationConnectionState state)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != state;
            _state = state;
        }
        if (changed)
        {
            try { StateChanged?.Invoke(state); } catch (Exception ex) { _options.Log?.Invoke("StateChanged handler failed: " + ex.Message); }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = MinRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            SetState(IntegrationConnectionState.Connecting);
            var client = new IntegrationClient(_options);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += _ => closed.TrySetResult();
            client.EventReceived += e =>
            {
                try { EventReceived?.Invoke(e); } catch (Exception ex) { _options.Log?.Invoke("Event handler failed: " + ex.Message); }
            };

            TimeSpan wait;
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                if (_events.Count > 0) await client.SubscribeAsync(_events, ct).ConfigureAwait(false);
                _client = client;
                IncompatibleVersion = null;
                delay = MinRetryDelay;
                SetState(IntegrationConnectionState.Connected);
                await closed.Task.WaitAsync(ct).ConfigureAwait(false);
                _options.Log?.Invoke("Integration connection closed; will reconnect");
                wait = MinRetryDelay;
                SetState(IntegrationConnectionState.Unavailable);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IntegrationException ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                switch (ex.Code)
                {
                    case IntegrationErrorCodes.IncompatibleVersion:
                        IncompatibleVersion = ex.Message;
                        SetState(IntegrationConnectionState.Incompatible);
                        wait = IncompatibleRetryDelay;
                        break;
                    case IntegrationErrorCodes.AccessDenied:
                        SetState(IntegrationConnectionState.AccessDenied);
                        wait = MaxRetryDelay;
                        break;
                    default:
                        SetState(IntegrationConnectionState.Unavailable);
                        wait = delay;
                        delay = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, delay.Ticks * 2));
                        break;
                }
            }
            finally
            {
                if (ReferenceEquals(_client, client) && !client.IsConnected) _client = null;
            }

            await client.DisposeAsync().ConfigureAwait(false);
            try
            {
                if (await _wake.WaitAsync(wait, ct).ConfigureAwait(false)) delay = MinRetryDelay;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        SetState(IntegrationConnectionState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_client is { } c) await c.DisposeAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { }
        }
        _cts.Dispose();
    }
}
