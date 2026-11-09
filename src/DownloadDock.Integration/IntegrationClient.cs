using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace DownloadDock.Integration;

public sealed class IntegrationClientOptions
{
    public required string PipeName { get; init; }
    public required string ClientName { get; init; }
    public string? ClientVersion { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxMessageBytes { get; init; } = IntegrationProtocol.DefaultMaxMessageBytes;
    public int MaxPendingRequests { get; init; } = 64;
    public Action<string>? Log { get; init; }
}

/// <summary>An event delivered to a client.</summary>
public sealed class IntegrationEvent
{
    internal IntegrationEvent(string name, long seq, JsonElement? payload)
    {
        Name = name;
        Seq = seq;
        Payload = payload;
    }

    public string Name { get; }

    /// <summary>Server-wide sequence number; a gap means events were dropped for this client.</summary>
    public long Seq { get; }

    public JsonElement? Payload { get; }

    public T? GetPayload<T>() => IntegrationJson.FromElement<T>(Payload);
}

/// <summary>
/// One connection to an integration server. Thread-safe; any number of requests may be in flight.
/// Never throws anything but <see cref="IntegrationException"/> from its public methods (plus
/// OperationCanceledException for caller cancellation).
/// </summary>
public sealed class IntegrationClient : IAsyncDisposable
{
    private readonly IntegrationClientOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IntegrationMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private Task? _readLoop;
    private long _nextId;
    private int _disconnected;

    public IntegrationClient(IntegrationClientOptions options) => _options = options;

    /// <summary>Raised on the client's read thread, in order. Keep handlers short (or hand off).</summary>
    public event Action<IntegrationEvent>? EventReceived;

    /// <summary>Raised once when the connection ends (server closed, crashed or <see cref="DisposeAsync"/>).</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _pipe is { IsConnected: true } && Volatile.Read(ref _disconnected) == 0 && Server is not null;

    /// <summary>The server's hello result (negotiated version, capabilities, events, process id).</summary>
    public HelloResult? Server { get; private set; }

    /// <summary>
    /// Connects and performs the version handshake. Throws IntegrationException: unavailable (no server),
    /// timeout, incompatibleVersion, accessDenied.
    /// </summary>
    public async Task<HelloResult> ConnectAsync(CancellationToken ct = default)
    {
        if (_pipe is not null) throw new InvalidOperationException("Already connected; create a new client to reconnect.");
        // CurrentUserOnly: on Windows the client verifies the server pipe is owned by the current user
        // (defeats a pipe squatted by another account).
        _pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await _pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new IntegrationException(IntegrationErrorCodes.Unavailable, $"No integration server is listening on '{_options.PipeName}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IntegrationException(IntegrationErrorCodes.AccessDenied, "Access to the integration pipe was denied (different user or untrusted owner).", ex);
        }
        catch (IOException ex)
        {
            throw new IntegrationException(IntegrationErrorCodes.Unavailable, "Could not connect: " + ex.Message, ex);
        }

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
        var hello = new HelloRequest
        {
            ProtocolVersion = IntegrationProtocol.Version,
            ClientName = _options.ClientName,
            ClientVersion = _options.ClientVersion,
            Capabilities = _options.Capabilities.ToList(),
        };
        var response = await SendRawAsync(IntegrationCommands.Hello, hello, _options.RequestTimeout, ct, allowBeforeHello: true).ConfigureAwait(false);
        var result = IntegrationJson.FromElement<HelloResult>(response.Result)
                     ?? throw new IntegrationException(IntegrationErrorCodes.InvalidMessage, "Empty hello result.");
        if (VersionNegotiation.Negotiate(result.ProtocolVersion) is null)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new IntegrationException(IntegrationErrorCodes.IncompatibleVersion, $"Server speaks protocol {result.ProtocolVersion}.");
        }
        Server = result;
        return result;
    }

    /// <summary>Sends a request and returns the deserialised result.</summary>
    public async Task<TResult?> SendAsync<TResult>(string command, object? payload = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await SendRawAsync(command, payload, timeout ?? _options.RequestTimeout, ct, allowBeforeHello: false).ConfigureAwait(false);
        try
        {
            return IntegrationJson.FromElement<TResult>(response.Result);
        }
        catch (JsonException ex)
        {
            throw new IntegrationException(IntegrationErrorCodes.InvalidMessage, "Unexpected result shape: " + ex.Message, ex);
        }
    }

    public Task<SubscribeResult?> SubscribeAsync(IEnumerable<string> events, CancellationToken ct = default)
        => SendAsync<SubscribeResult>(IntegrationCommands.Subscribe, new SubscribeRequest { Events = events.ToList() }, null, ct);

    private async Task<IntegrationMessage> SendRawAsync(string command, object? payload, TimeSpan timeout, CancellationToken ct, bool allowBeforeHello)
    {
        var pipe = _pipe;
        if (pipe is null || Volatile.Read(ref _disconnected) != 0 || (!allowBeforeHello && Server is null))
            throw new IntegrationException(IntegrationErrorCodes.Disconnected, "Not connected.");
        if (_pending.Count >= _options.MaxPendingRequests)
            throw new IntegrationException(IntegrationErrorCodes.Busy, "Too many requests in flight.");

        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<IntegrationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await MessageFraming.WriteAsync(pipe, IntegrationMessage.Request(id, command, payload), _options.MaxMessageBytes, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new IntegrationException(IntegrationErrorCodes.Disconnected, "Connection lost while sending.", ex);
            }
            catch (ObjectDisposedException ex)
            {
                throw new IntegrationException(IntegrationErrorCodes.Disconnected, "Connection closed.", ex);
            }
            finally
            {
                _writeLock.Release();
            }

            IntegrationMessage response;
            try
            {
                response = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new IntegrationException(IntegrationErrorCodes.Timeout, $"{command} timed out after {timeout.TotalSeconds:0.#} s.");
            }
            if (response.Ok == true) return response;
            var error = response.Error ?? new IntegrationError { Code = IntegrationErrorCodes.InternalError, Message = "Request failed." };
            throw new IntegrationException(error.Code, error.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? reason = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await MessageFraming.ReadAsync(_pipe!, _options.MaxMessageBytes, ct).ConfigureAwait(false);
                if (message is null) break;
                switch (message.Type)
                {
                    case MessageTypes.Response when message.Id is not null && _pending.TryGetValue(message.Id, out var tcs):
                        tcs.TrySetResult(message);
                        break;
                    case MessageTypes.Response when message.Id is null && message.Ok == false:
                        // Connection-level error (e.g. message too large / handshake refused): fail everything.
                        reason = new IntegrationException(message.Error?.Code ?? IntegrationErrorCodes.InternalError, message.Error?.Message ?? "Server error.");
                        foreach (var p in _pending.Values) p.TrySetResult(message);
                        break;
                    case MessageTypes.Event when message.Event is not null:
                        try
                        {
                            EventReceived?.Invoke(new IntegrationEvent(message.Event, message.Seq ?? 0, message.Payload));
                        }
                        catch (Exception ex)
                        {
                            _options.Log?.Invoke("Event handler failed: " + ex.Message);
                        }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or IntegrationException)
        {
            reason ??= ex is OperationCanceledException ? null : ex;
        }
        finally
        {
            MarkDisconnected(reason);
        }
    }

    private void MarkDisconnected(Exception? reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        var error = new IntegrationException(IntegrationErrorCodes.Disconnected, "The connection was closed.", reason);
        foreach (var p in _pending.Values) p.TrySetException(error);
        try { Disconnected?.Invoke(reason); } catch (Exception ex) { _options.Log?.Invoke("Disconnected handler failed: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
        }
        MarkDisconnected(null);
    }
}
