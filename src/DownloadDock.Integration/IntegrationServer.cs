using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace DownloadDock.Integration;

public sealed class IntegrationServerOptions
{
    public required string PipeName { get; init; }
    public required string ServerName { get; init; }
    public string? ServerVersion { get; init; }

    /// <summary>Capabilities advertised in hello (normally the command names this server handles).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>Event names clients may subscribe to.</summary>
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();

    public int MaxConnections { get; init; } = 8;
    public int MaxMessageBytes { get; init; } = IntegrationProtocol.DefaultMaxMessageBytes;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxConcurrentRequestsPerConnection { get; init; } = 16;

    /// <summary>Events queued per connection before new ones are dropped (slow or stalled reader).</summary>
    public int EventQueueCapacity { get; init; } = 256;

    /// <summary>Optional diagnostics sink (never receives payload contents).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>A connected, handshaken client.</summary>
public sealed class IntegrationPeer
{
    internal IntegrationPeer(int id) => Id = id;

    public int Id { get; }
    public string ClientName { get; internal set; } = "";
    public string? ClientVersion { get; internal set; }
    public string ProtocolVersion { get; internal set; } = "";
    public IReadOnlyList<string> Capabilities { get; internal set; } = Array.Empty<string>();

    /// <summary>Client process id as reported by Windows (null where unavailable). Identification only.</summary>
    public int? ProcessId { get; internal set; }

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    internal readonly HashSet<string> SubscriptionSet = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Subscriptions
    {
        get { lock (SubscriptionSet) return SubscriptionSet.ToArray(); }
    }

    public bool IsSubscribed(string eventName)
    {
        lock (SubscriptionSet) return SubscriptionSet.Contains(eventName);
    }

    /// <summary>Events dropped because this client wasn't reading fast enough.</summary>
    public long DroppedEvents => Interlocked.Read(ref DroppedCounter);

    internal long DroppedCounter;
}

public sealed class IntegrationRequestContext
{
    internal IntegrationRequestContext(IntegrationPeer peer, string command, JsonElement? payload, CancellationToken ct)
    {
        Peer = peer;
        Command = command;
        Payload = payload;
        CancellationToken = ct;
    }

    public IntegrationPeer Peer { get; }
    public string Command { get; }
    public JsonElement? Payload { get; }

    /// <summary>Cancelled when the request times out or the connection closes.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Deserialises the payload; a missing or malformed payload raises invalidArgument.</summary>
    public T GetPayload<T>() where T : new()
    {
        try
        {
            return IntegrationJson.FromElement<T>(Payload) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new IntegrationException(IntegrationErrorCodes.InvalidArgument, "Invalid payload: " + ex.Message, ex);
        }
    }
}

/// <summary>Handles one command. Return the result object (serialised as "result"), or throw <see cref="IntegrationException"/>.</summary>
public delegate Task<object?> IntegrationHandler(IntegrationRequestContext context);

/// <summary>
/// Named-pipe server for the integration protocol. Runs entirely on background threads – handlers that
/// touch UI must marshal to their dispatcher themselves (asynchronously). Connection failures never
/// propagate to the host application.
/// </summary>
public sealed class IntegrationServer : IAsyncDisposable
{
    private readonly IntegrationServerOptions _options;
    private readonly ConcurrentDictionary<string, IntegrationHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _nextPeerId;
    private long _eventSeq;
    private bool _firstInstance = true;

    public IntegrationServer(IntegrationServerOptions options)
    {
        if (options.MaxMessageBytes is <= 0 or > IntegrationProtocol.AbsoluteMaxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMessageBytes is out of range.");
        _options = options;
    }

    /// <summary>Raised (on a background thread) after a client completes the handshake.</summary>
    public event Action<IntegrationPeer>? PeerConnected;

    /// <summary>Raised (on a background thread) when a handshaken client disconnects.</summary>
    public event Action<IntegrationPeer>? PeerDisconnected;

    public string PipeName => _options.PipeName;

    public bool IsListening { get; private set; }

    /// <summary>Set when the pipe name is already owned by another process (possible squatting).</summary>
    public string? StartupProblem { get; private set; }

    public IReadOnlyList<IntegrationPeer> Peers => _connections.Values.Where(c => c.Peer.ProtocolVersion.Length > 0).Select(c => c.Peer).ToList();

    public void Handle(string command, IntegrationHandler handler)
    {
        if (command.StartsWith("system.", StringComparison.Ordinal))
            throw new ArgumentException("system.* commands are built in.", nameof(command));
        _handlers[command] = handler;
    }

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>True when any handshaken client (optionally with the given client name) subscribed to the event.</summary>
    public bool HasSubscriber(string eventName, string? clientName = null)
        => Peers.Any(p => p.IsSubscribed(eventName) && (clientName is null || string.Equals(p.ClientName, clientName, StringComparison.Ordinal)));

    /// <summary>Queues an event to every subscribed client. Never blocks; slow clients drop events (see seq).</summary>
    public void Publish(string eventName, object? payload)
    {
        var subscribers = _connections.Values.Where(c => c.Peer.IsSubscribed(eventName)).ToList();
        if (subscribers.Count == 0) return;
        var message = IntegrationMessage.EventMessage(eventName, Interlocked.Increment(ref _eventSeq), payload);
        foreach (var c in subscribers) c.EnqueueEvent(message);
    }

    // ------------------------------------------------------------------ accept loop

    private NamedPipeServerStream CreatePipe()
    {
        var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (OperatingSystem.IsWindows())
        {
            if (_firstInstance) options |= PipeOptions.FirstPipeInstance;
            return NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, _options.MaxConnections,
                PipeTransmissionMode.Byte, options, 64 * 1024, 64 * 1024, PipeSecurityFactory.Create());
        }
        return new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut, _options.MaxConnections,
            PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                _firstInstance = false;
                IsListening = true;
                StartupProblem = null;
                failures = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Either all instances are busy, or (on first creation) another process owns the name.
                if (_firstInstance) StartupProblem = "The integration pipe is already in use by another process.";
                _options.Log?.Invoke("Pipe creation failed: " + ex.Message);
                failures++;
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 250 * failures)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                continue;
            }

            if (_connections.Count >= _options.MaxConnections)
            {
                _options.Log?.Invoke("Connection refused: limit reached");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var connection = new Connection(this, pipe, Interlocked.Increment(ref _nextPeerId));
            _connections[connection.Peer.Id] = connection;
            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.RunAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _options.Log?.Invoke("Connection failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    _connections.TryRemove(connection.Peer.Id, out _);
                    if (connection.Peer.ProtocolVersion.Length > 0)
                    {
                        try { PeerDisconnected?.Invoke(connection.Peer); } catch (Exception ex) { _options.Log?.Invoke("PeerDisconnected handler failed: " + ex.Message); }
                    }
                }
            }, CancellationToken.None);
        }
        IsListening = false;
    }

    // ------------------------------------------------------------------ connection

    private sealed class Connection
    {
        private readonly IntegrationServer _server;
        private readonly NamedPipeServerStream _pipe;
        private readonly Channel<IntegrationMessage> _outbox = Channel.CreateUnbounded<IntegrationMessage>(new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim _inflight;
        private int _queuedEvents;

        public Connection(IntegrationServer server, NamedPipeServerStream pipe, int id)
        {
            _server = server;
            _pipe = pipe;
            Peer = new IntegrationPeer(id) { ProcessId = ClientProcessId(pipe) };
            _inflight = new SemaphoreSlim(server._options.MaxConcurrentRequestsPerConnection);
        }

        public IntegrationPeer Peer { get; }

        private IntegrationServerOptions Options => _server._options;

        public void EnqueueEvent(IntegrationMessage message)
        {
            if (Interlocked.Increment(ref _queuedEvents) > Options.EventQueueCapacity)
            {
                Interlocked.Decrement(ref _queuedEvents);
                Interlocked.Increment(ref Peer.DroppedCounter);
                return;
            }
            if (!_outbox.Writer.TryWrite(message)) Interlocked.Decrement(ref _queuedEvents);
        }

        private void Send(IntegrationMessage message) => _outbox.Writer.TryWrite(message);

        public async Task RunAsync(CancellationToken serverToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            var writer = Task.Run(() => WriteLoopAsync(cts.Token), CancellationToken.None);
            try
            {
                if (!await HandshakeAsync(cts.Token).ConfigureAwait(false)) return;
                try { _server.PeerConnected?.Invoke(Peer); } catch (Exception ex) { Options.Log?.Invoke("PeerConnected handler failed: " + ex.Message); }

                while (!cts.IsCancellationRequested)
                {
                    IntegrationMessage? message;
                    try
                    {
                        message = await MessageFraming.ReadAsync(_pipe, Options.MaxMessageBytes, cts.Token).ConfigureAwait(false);
                    }
                    catch (IntegrationException ex) when (ex.Code == IntegrationErrorCodes.InvalidMessage)
                    {
                        Send(IntegrationMessage.Failure(null, ex.Code, ex.Message)); // framing intact: keep going
                        continue;
                    }
                    catch (IntegrationException ex) when (ex.Code == IntegrationErrorCodes.MessageTooLarge)
                    {
                        Send(IntegrationMessage.Failure(null, ex.Code, ex.Message));
                        if (ex.Data[MessageFraming.StreamIntactKey] is true) continue; // body skipped: still in sync
                        return; // absurd length: can't resynchronise safely, close
                    }
                    if (message is null) return; // client closed
                    Dispatch(message, cts.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or IntegrationException)
            {
                // Disconnects are normal.
            }
            finally
            {
                _outbox.Writer.TryComplete();
                try { await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
                cts.Cancel();
                await _pipe.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task<bool> HandshakeAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Options.HandshakeTimeout);
            IntegrationMessage? first;
            try
            {
                first = await MessageFraming.ReadAsync(_pipe, Options.MaxMessageBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Options.Log?.Invoke("Handshake timed out");
                return false;
            }
            catch (IntegrationException ex)
            {
                Send(IntegrationMessage.Failure(null, ex.Code, ex.Message));
                return false;
            }
            if (first is null) return false;
            if (first.Type != MessageTypes.Request || first.Command != IntegrationCommands.Hello || string.IsNullOrEmpty(first.Id))
            {
                Send(IntegrationMessage.Failure(first.Id, IntegrationErrorCodes.HandshakeRequired, "The first message must be a system.hello request."));
                return false;
            }

            HelloRequest hello;
            try
            {
                hello = IntegrationJson.FromElement<HelloRequest>(first.Payload) ?? new HelloRequest();
            }
            catch (JsonException)
            {
                Send(IntegrationMessage.Failure(first.Id, IntegrationErrorCodes.InvalidArgument, "Invalid hello payload."));
                return false;
            }
            var negotiated = VersionNegotiation.Negotiate(hello.ProtocolVersion);
            if (negotiated is null)
            {
                Send(IntegrationMessage.Failure(first.Id, IntegrationErrorCodes.IncompatibleVersion,
                    $"Protocol {hello.ProtocolVersion} is not compatible with {IntegrationProtocol.Version} (major versions must match)."));
                return false;
            }
            if (string.IsNullOrWhiteSpace(hello.ClientName) || hello.ClientName.Length > 64)
            {
                Send(IntegrationMessage.Failure(first.Id, IntegrationErrorCodes.InvalidArgument, "clientName is required (max 64 characters)."));
                return false;
            }

            Peer.ClientName = hello.ClientName;
            Peer.ClientVersion = hello.ClientVersion is { Length: <= 64 } v ? v : null;
            Peer.Capabilities = hello.Capabilities.Take(64).Where(c => c.Length <= 128).ToList();
            Peer.ProtocolVersion = negotiated;
            Send(IntegrationMessage.Success(first.Id, new HelloResult
            {
                ProtocolVersion = negotiated,
                ServerName = Options.ServerName,
                ServerVersion = Options.ServerVersion,
                Capabilities = Options.Capabilities.ToList(),
                Events = Options.Events.ToList(),
                ProcessId = Environment.ProcessId,
                MaxMessageBytes = Options.MaxMessageBytes,
            }));
            Options.Log?.Invoke($"Client connected: {Peer.ClientName} {Peer.ClientVersion} (pid {Peer.ProcessId?.ToString() ?? "?"}, protocol {negotiated})");
            return true;
        }

        private void Dispatch(IntegrationMessage message, CancellationToken ct)
        {
            if (message.Type != MessageTypes.Request)
                return; // clients don't send responses/events to a server; ignore
            if (string.IsNullOrEmpty(message.Id) || message.Id.Length > 64 || string.IsNullOrEmpty(message.Command) || message.Command.Length > 128)
            {
                Send(IntegrationMessage.Failure(message.Id is { Length: <= 64 } ? message.Id : null, IntegrationErrorCodes.InvalidMessage, "Requests need an id (≤64 chars) and a command."));
                return;
            }
            if (!_inflight.Wait(0))
            {
                Send(IntegrationMessage.Failure(message.Id, IntegrationErrorCodes.Busy, "Too many concurrent requests on this connection."));
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await ExecuteAsync(message, ct).ConfigureAwait(false);
                    Send(IntegrationMessage.Success(message.Id!, result));
                }
                catch (IntegrationException ex)
                {
                    Send(IntegrationMessage.Failure(message.Id, ex.Code, ex.Message));
                }
                catch (Exception ex)
                {
                    Options.Log?.Invoke($"Handler for {message.Command} failed: {ex.GetType().Name}: {ex.Message}");
                    Send(IntegrationMessage.Failure(message.Id, IntegrationErrorCodes.InternalError, "The command failed."));
                }
                finally
                {
                    _inflight.Release();
                }
            }, CancellationToken.None);
        }

        private async Task<object?> ExecuteAsync(IntegrationMessage message, CancellationToken ct)
        {
            switch (message.Command)
            {
                case IntegrationCommands.Hello:
                    throw new IntegrationException(IntegrationErrorCodes.InvalidMessage, "Already connected.");
                case IntegrationCommands.Ping:
                    return new PingResult();
                case IntegrationCommands.Subscribe:
                case IntegrationCommands.Unsubscribe:
                {
                    var request = IntegrationJson.FromElement<SubscribeRequest>(message.Payload) ?? new SubscribeRequest();
                    var known = request.Events.Where(e => Options.Events.Contains(e)).Distinct().ToList();
                    var unknown = request.Events.Except(known).Take(32).ToList();
                    lock (Peer.SubscriptionSet)
                    {
                        foreach (var e in known)
                        {
                            if (message.Command == IntegrationCommands.Subscribe) Peer.SubscriptionSet.Add(e);
                            else Peer.SubscriptionSet.Remove(e);
                        }
                    }
                    return new SubscribeResult { Subscribed = Peer.Subscriptions.ToList(), Unknown = unknown };
                }
            }

            if (!_server._handlers.TryGetValue(message.Command!, out var handler))
                throw new IntegrationException(IntegrationErrorCodes.UnknownCommand, $"Unknown command '{message.Command}'.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Options.RequestTimeout);
            var context = new IntegrationRequestContext(Peer, message.Command!, message.Payload, timeout.Token);
            try
            {
                return await handler(context).WaitAsync(Options.RequestTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new IntegrationException(IntegrationErrorCodes.Timeout, $"{message.Command} did not complete within {Options.RequestTimeout.TotalSeconds:0.#} s.");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new IntegrationException(IntegrationErrorCodes.Timeout, $"{message.Command} timed out.");
            }
        }

        private async Task WriteLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (message.Type == MessageTypes.Event) Interlocked.Decrement(ref _queuedEvents);
                    try
                    {
                        await MessageFraming.WriteAsync(_pipe, message, Options.MaxMessageBytes, ct).ConfigureAwait(false);
                    }
                    catch (IntegrationException ex) when (ex.Code == IntegrationErrorCodes.MessageTooLarge && message.Type == MessageTypes.Response)
                    {
                        await MessageFraming.WriteAsync(_pipe, IntegrationMessage.Failure(message.Id, ex.Code, "The response was too large."), Options.MaxMessageBytes, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or IntegrationException)
            {
            }
        }

        private static int? ClientProcessId(NamedPipeServerStream pipe)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : null;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { }
        }
        // Close live connections (their loops observe the cancellation).
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!_connections.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(20).ConfigureAwait(false);
        _cts.Dispose();
    }
}
