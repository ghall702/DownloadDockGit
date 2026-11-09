using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace DownloadDock.Integration.Tests;

/// <summary>Real named-pipe server/client behaviour (Unix domain sockets on Linux, named pipes on Windows).</summary>
public class ServerClientTests
{
    internal static string UniquePipe() => "ddtest." + Guid.NewGuid().ToString("N")[..12];

    internal static IntegrationServer NewServer(string pipe, Action<IntegrationServer>? configure = null, TimeSpan? requestTimeout = null,
        int maxConnections = 8, int maxMessageBytes = IntegrationProtocol.DefaultMaxMessageBytes, int eventQueue = 256, TimeSpan? handshake = null,
        int maxConcurrent = 16)
    {
        var server = new IntegrationServer(new IntegrationServerOptions
        {
            PipeName = pipe,
            ServerName = "TestServer",
            ServerVersion = "9.9",
            Capabilities = new[] { "test.echo", "test.slow", "test.fail", "test.crash", "test.big" },
            Events = IntegrationEvents.All,
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10),
            MaxConnections = maxConnections,
            MaxMessageBytes = maxMessageBytes,
            EventQueueCapacity = eventQueue,
            HandshakeTimeout = handshake ?? TimeSpan.FromSeconds(5),
            MaxConcurrentRequestsPerConnection = maxConcurrent,
        });
        server.Handle("test.echo", ctx => Task.FromResult<object?>(ctx.GetPayload<OpenFolderRequest>()));
        server.Handle("test.slow", async ctx => { await Task.Delay(TimeSpan.FromSeconds(30), ctx.CancellationToken); return null; });
        server.Handle("test.fail", _ => throw new IntegrationException(IntegrationErrorCodes.InvalidPath, "bad path"));
        server.Handle("test.crash", _ => throw new InvalidOperationException("secret internal detail"));
        server.Handle("test.big", _ => Task.FromResult<object?>(new string('x', 200_000)));
        configure?.Invoke(server);
        server.Start();
        return server;
    }

    internal static async Task<IntegrationClient> Connect(string pipe, TimeSpan? requestTimeout = null, string name = "TestClient")
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var client = new IntegrationClient(new IntegrationClientOptions
            {
                PipeName = pipe, ClientName = name, ClientVersion = "1.2.3", RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5),
            });
            try
            {
                await client.ConnectAsync();
                return client;
            }
            catch (IntegrationException) when (DateTime.UtcNow < deadline)
            {
                await client.DisposeAsync();
                await Task.Delay(50);
            }
        }
    }

    internal static async Task<bool> WaitFor(Func<bool> condition, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Handshake_reports_server_identity_capabilities_and_version()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        IntegrationPeer? peer = null;
        server.PeerConnected += p => peer = p;
        await using var client = await Connect(pipe);
        Assert.Equal("TestServer", client.Server!.ServerName);
        Assert.Equal("9.9", client.Server.ServerVersion);
        Assert.Equal(IntegrationProtocol.Version, client.Server.ProtocolVersion);
        Assert.Contains("test.echo", client.Server.Capabilities);
        Assert.Contains(IntegrationEvents.ScreenshotCaptured, client.Server.Events);
        Assert.Equal(Environment.ProcessId, client.Server.ProcessId);
        Assert.True(await WaitFor(() => peer is not null));
        Assert.Equal("TestClient", peer!.ClientName);
        Assert.Equal("1.2.3", peer.ClientVersion);
    }

    [Fact]
    public async Task Commands_are_dispatched_with_their_payloads()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var client = await Connect(pipe);
        var echoed = await client.SendAsync<OpenFolderRequest>("test.echo", new OpenFolderRequest { Path = @"D:\Ünicode\Folder", NewWindow = true });
        Assert.Equal(@"D:\Ünicode\Folder", echoed!.Path);
        Assert.True(echoed.NewWindow);
        Assert.NotNull(await client.SendAsync<PingResult>(IntegrationCommands.Ping));
    }

    [Fact]
    public async Task Unknown_commands_and_handler_errors_return_error_codes_without_leaking_details()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var client = await Connect(pipe);
        Assert.Equal(IntegrationErrorCodes.UnknownCommand, (await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>("nope.command"))).Code);
        Assert.Equal(IntegrationErrorCodes.InvalidPath, (await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>("test.fail"))).Code);
        var crash = await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>("test.crash"));
        Assert.Equal(IntegrationErrorCodes.InternalError, crash.Code);
        Assert.DoesNotContain("secret", crash.Message);
        // The connection survives errors.
        Assert.NotNull(await client.SendAsync<PingResult>(IntegrationCommands.Ping));
    }

    [Fact]
    public async Task Server_times_out_slow_handlers()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, requestTimeout: TimeSpan.FromMilliseconds(300));
        await using var client = await Connect(pipe);
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>("test.slow"));
        Assert.Equal(IntegrationErrorCodes.Timeout, ex.Code);
    }

    [Fact]
    public async Task Client_times_out_requests_it_is_not_willing_to_wait_for()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var client = await Connect(pipe, requestTimeout: TimeSpan.FromMilliseconds(300));
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>("test.slow"));
        Assert.Equal(IntegrationErrorCodes.Timeout, ex.Code);
        Assert.NotNull(await client.SendAsync<PingResult>(IntegrationCommands.Ping)); // still usable
    }

    [Fact]
    public async Task Connecting_without_a_server_fails_fast_with_unavailable()
    {
        await using var client = new IntegrationClient(new IntegrationClientOptions { PipeName = UniquePipe(), ClientName = "x", ConnectTimeout = TimeSpan.FromMilliseconds(200) });
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => client.ConnectAsync());
        Assert.Equal(IntegrationErrorCodes.Unavailable, ex.Code);
    }

    // ------------------------------------------------------------------ raw protocol abuse

    private static async Task<NamedPipeClientStream> Raw(string pipe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await raw.ConnectAsync(1000);
                return raw;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await raw.DisposeAsync();
                await Task.Delay(50);
            }
        }
    }

    private static Task SendRaw(Stream s, IntegrationMessage m) => MessageFraming.WriteAsync(s, m, IntegrationProtocol.AbsoluteMaxMessageBytes, default);

    private static Task<IntegrationMessage?> ReadRaw(Stream s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return MessageFraming.ReadAsync(s, IntegrationProtocol.AbsoluteMaxMessageBytes, cts.Token);
    }

    private static IntegrationMessage Hello(string version = "1.0") => IntegrationMessage.Request("h", IntegrationCommands.Hello,
        new HelloRequest { ProtocolVersion = version, ClientName = "Raw" });

    [Fact]
    public async Task First_message_must_be_hello()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var raw = await Raw(pipe);
        await SendRaw(raw, IntegrationMessage.Request("1", "test.echo", null));
        var reply = await ReadRaw(raw);
        Assert.Equal(IntegrationErrorCodes.HandshakeRequired, reply!.Error!.Code);
        Assert.Null(await ReadRaw(raw)); // closed
    }

    [Fact]
    public async Task Incompatible_major_version_is_refused()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var raw = await Raw(pipe);
        await SendRaw(raw, Hello("2.0"));
        var reply = await ReadRaw(raw);
        Assert.False(reply!.Ok);
        Assert.Equal(IntegrationErrorCodes.IncompatibleVersion, reply.Error!.Code);
        Assert.Null(await ReadRaw(raw));
    }

    [Fact]
    public async Task Newer_minor_version_is_accepted_and_negotiated_down()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var raw = await Raw(pipe);
        await SendRaw(raw, Hello("1.9"));
        var reply = await ReadRaw(raw);
        Assert.True(reply!.Ok);
        Assert.Equal(IntegrationProtocol.Version, IntegrationJson.FromElement<HelloResult>(reply.Result)!.ProtocolVersion);
    }

    [Fact]
    public async Task Silent_connections_are_dropped_after_the_handshake_timeout()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, handshake: TimeSpan.FromMilliseconds(400));
        await using var raw = await Raw(pipe);
        Assert.Null(await ReadRaw(raw)); // server hangs up without us sending anything
    }

    [Fact]
    public async Task Malformed_json_gets_an_error_and_the_connection_continues()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var raw = await Raw(pipe);
        await SendRaw(raw, Hello());
        Assert.True((await ReadRaw(raw))!.Ok);
        var garbage = Encoding.UTF8.GetBytes("{ this is not json");
        var frame = new byte[4 + garbage.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)garbage.Length);
        garbage.CopyTo(frame, 4);
        await raw.WriteAsync(frame);
        Assert.Equal(IntegrationErrorCodes.InvalidMessage, (await ReadRaw(raw))!.Error!.Code);
        await SendRaw(raw, IntegrationMessage.Request("p", IntegrationCommands.Ping, null));
        Assert.True((await ReadRaw(raw))!.Ok);
    }

    [Fact]
    public async Task Oversized_messages_are_refused_and_skipped()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, maxMessageBytes: 4096);
        await using var raw = await Raw(pipe);
        await SendRaw(raw, Hello());
        Assert.True((await ReadRaw(raw))!.Ok);
        await SendRaw(raw, IntegrationMessage.Request("big", "test.echo", new OpenFolderRequest { Path = new string('a', 10_000) }));
        Assert.Equal(IntegrationErrorCodes.MessageTooLarge, (await ReadRaw(raw))!.Error!.Code);
        // The body was skipped, so the connection is still in sync and usable.
        await SendRaw(raw, IntegrationMessage.Request("p", IntegrationCommands.Ping, null));
        Assert.True((await ReadRaw(raw))!.Ok);
    }

    [Fact]
    public async Task Absurd_message_lengths_close_the_connection_without_allocating()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var raw = await Raw(pipe);
        await SendRaw(raw, Hello());
        Assert.True((await ReadRaw(raw))!.Ok);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 2_000_000_000);
        await raw.WriteAsync(header);
        await raw.FlushAsync();
        IntegrationMessage? reply = null;
        try { reply = await ReadRaw(raw); } catch (IOException) { }
        if (reply is not null)
        {
            Assert.Equal(IntegrationErrorCodes.MessageTooLarge, reply.Error!.Code);
            try { Assert.Null(await ReadRaw(raw)); } catch (IOException) { }
        }
        await using var fresh = await Connect(pipe); // the server is alive and still accepts new clients
        Assert.NotNull(await fresh.SendAsync<PingResult>(IntegrationCommands.Ping));
    }

    [Fact]
    public async Task Oversized_responses_become_errors()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, maxMessageBytes: 8192);
        await using var client = await Connect(pipe);
        Assert.Equal(IntegrationErrorCodes.MessageTooLarge, (await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<string>("test.big"))).Code);
    }

    // ------------------------------------------------------------------ concurrency & limits

    [Fact]
    public async Task Many_simultaneous_requests_get_their_own_responses()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, maxConcurrent: 16);
        // Several clients at once, each with 16 requests in flight, 300 requests in total.
        var clients = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Connect(pipe)));
        var tasks = clients.SelectMany((client, c) => Enumerable.Range(0, 100).Select(i => (client, c, i))).GroupBy(x => x.c).Select(async group =>
        {
            using var gate = new SemaphoreSlim(16);
            var results = await Task.WhenAll(group.Select(async x =>
            {
                await gate.WaitAsync();
                try
                {
                    var r = await x.client.SendAsync<OpenFolderRequest>("test.echo", new OpenFolderRequest { Path = $@"C:\c{x.c}\folder{x.i}" });
                    return ($@"C:\c{x.c}\folder{x.i}", r!.Path);
                }
                finally { gate.Release(); }
            }));
            return results;
        });
        foreach (var (expected, actual) in (await Task.WhenAll(tasks)).SelectMany(r => r))
            Assert.Equal(expected, actual);
        foreach (var c in clients) await c.DisposeAsync();
    }

    [Fact]
    public async Task Client_bounds_its_own_pending_requests()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, maxConcurrent: 64, requestTimeout: TimeSpan.FromSeconds(2));
        await using var client = await Connect(pipe, requestTimeout: TimeSpan.FromSeconds(3));
        var slow = Enumerable.Range(0, 64).Select(_ => client.SendAsync<object>("test.slow")).ToList();
        await Task.Delay(200);
        Assert.Equal(IntegrationErrorCodes.Busy, (await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>(IntegrationCommands.Ping))).Code);
        foreach (var s in slow) await Assert.ThrowsAsync<IntegrationException>(() => s);
    }

    [Fact]
    public async Task Too_many_concurrent_requests_on_one_connection_are_refused_with_busy()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, maxConcurrent: 2, requestTimeout: TimeSpan.FromSeconds(3));
        await using var client = await Connect(pipe, requestTimeout: TimeSpan.FromSeconds(5));
        var slow = Enumerable.Range(0, 2).Select(_ => client.SendAsync<object>("test.slow")).ToList();
        await Task.Delay(300);
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>("test.echo", new OpenFolderRequest()));
        Assert.Equal(IntegrationErrorCodes.Busy, ex.Code);
        foreach (var s in slow) await Assert.ThrowsAsync<IntegrationException>(() => s);
    }

    [Fact]
    public async Task Connection_limit_is_enforced_and_frees_up()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, maxConnections: 2);
        var a = await Connect(pipe);
        var b = await Connect(pipe);
        var c = new IntegrationClient(new IntegrationClientOptions { PipeName = pipe, ClientName = "c", ConnectTimeout = TimeSpan.FromMilliseconds(300), RequestTimeout = TimeSpan.FromSeconds(1) });
        await Assert.ThrowsAsync<IntegrationException>(() => c.ConnectAsync());
        await c.DisposeAsync();
        await a.DisposeAsync();
        await using var d = await Connect(pipe); // a slot is free again
        Assert.NotNull(await d.SendAsync<PingResult>(IntegrationCommands.Ping));
        await b.DisposeAsync();
    }

    // ------------------------------------------------------------------ events

    [Fact]
    public async Task Events_reach_only_subscribers_in_order()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        await using var subscriber = await Connect(pipe);
        await using var bystander = await Connect(pipe);
        var received = new List<IntegrationEvent>();
        var other = new List<IntegrationEvent>();
        subscriber.EventReceived += e => { lock (received) received.Add(e); };
        bystander.EventReceived += e => { lock (other) other.Add(e); };

        var sub = await subscriber.SubscribeAsync(new[] { IntegrationEvents.ScreenshotCaptured, IntegrationEvents.FanOpened, "made.up" });
        Assert.Equal(new[] { "made.up" }, sub!.Unknown);
        Assert.True(server.HasSubscriber(IntegrationEvents.ScreenshotCaptured, "TestClient"));
        Assert.False(server.HasSubscriber(IntegrationEvents.DownloadDetected));

        for (var i = 0; i < 20; i++)
            server.Publish(IntegrationEvents.ScreenshotCaptured, new FileEventPayload { Path = $@"C:\s\{i}.png", Name = $"{i}.png" });
        server.Publish(IntegrationEvents.DownloadDetected, new FileEventPayload { Path = @"C:\d\x.pdf" }); // nobody subscribed

        Assert.True(await WaitFor(() => { lock (received) return received.Count == 20; }));
        lock (received)
        {
            Assert.Equal(Enumerable.Range(0, 20).Select(i => $@"C:\s\{i}.png"), received.Select(e => e.GetPayload<FileEventPayload>()!.Path));
            Assert.True(received.Zip(received.Skip(1)).All(p => p.Second.Seq > p.First.Seq));
        }
        Assert.Empty(other);

        await subscriber.SendAsync<SubscribeResult>(IntegrationCommands.Unsubscribe, new SubscribeRequest { Events = new() { IntegrationEvents.ScreenshotCaptured } });
        server.Publish(IntegrationEvents.ScreenshotCaptured, new FileEventPayload { Path = @"C:\late.png" });
        await Task.Delay(300);
        lock (received) Assert.Equal(20, received.Count);
    }

    [Fact]
    public async Task A_stalled_subscriber_cannot_block_the_server_or_grow_memory_without_bound()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe, eventQueue: 16);
        await using var raw = await Raw(pipe);
        await SendRaw(raw, Hello());
        Assert.True((await ReadRaw(raw))!.Ok);
        await SendRaw(raw, IntegrationMessage.Request("s", IntegrationCommands.Subscribe, new SubscribeRequest { Events = new() { IntegrationEvents.DownloadDetected } }));
        Assert.True((await ReadRaw(raw))!.Ok);

        // Now never read again. Publishing must stay instant and drop what can't be delivered.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var payload = new FileEventPayload { Path = @"C:\Downloads\" + new string('x', 2000) + ".bin" };
        for (var i = 0; i < 5000; i++) server.Publish(IntegrationEvents.DownloadDetected, payload);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"publishing took {sw.ElapsedMilliseconds} ms");
        var peer = Assert.Single(server.Peers);
        Assert.True(await WaitFor(() => peer.DroppedEvents > 4000), $"dropped {peer.DroppedEvents}");
    }

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public async Task Server_shutdown_fails_pending_requests_and_raises_disconnected()
    {
        var pipe = UniquePipe();
        var server = NewServer(pipe);
        var client = await Connect(pipe, requestTimeout: TimeSpan.FromSeconds(20));
        var disconnected = new TaskCompletionSource();
        client.Disconnected += _ => disconnected.TrySetResult();
        var pending = client.SendAsync<object>("test.slow");
        await Task.Delay(200);
        await server.DisposeAsync();
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => pending);
        Assert.Equal(IntegrationErrorCodes.Disconnected, ex.Code);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.IsConnected);
        Assert.Equal(IntegrationErrorCodes.Disconnected, (await Assert.ThrowsAsync<IntegrationException>(() => client.SendAsync<object>(IntegrationCommands.Ping))).Code);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Client_disconnect_is_seen_by_the_server_and_frees_its_subscriptions()
    {
        var pipe = UniquePipe();
        await using var server = NewServer(pipe);
        IntegrationPeer? gone = null;
        server.PeerDisconnected += p => gone = p;
        var client = await Connect(pipe);
        await client.SubscribeAsync(new[] { IntegrationEvents.FanOpened });
        Assert.True(server.HasSubscriber(IntegrationEvents.FanOpened));
        await client.DisposeAsync();
        Assert.True(await WaitFor(() => gone is not null));
        Assert.False(server.HasSubscriber(IntegrationEvents.FanOpened));
        server.Publish(IntegrationEvents.FanOpened, new FanStatePayload { Open = true }); // no crash
    }

    [Fact]
    public async Task Resilient_client_started_first_connects_when_the_server_appears_and_survives_restarts()
    {
        var pipe = UniquePipe();
        await using var resilient = new ResilientIntegrationClient(new IntegrationClientOptions
        {
            PipeName = pipe, ClientName = "FinderForWindows", ConnectTimeout = TimeSpan.FromMilliseconds(200),
        }, new[] { IntegrationEvents.ScreenshotCaptured })
        {
            MinRetryDelay = TimeSpan.FromMilliseconds(100),
            MaxRetryDelay = TimeSpan.FromMilliseconds(400),
        };
        var events = new List<string>();
        resilient.EventReceived += e => { lock (events) events.Add(e.GetPayload<FileEventPayload>()!.Path); };
        resilient.Start();

        // Client first, no server yet.
        Assert.True(await WaitFor(() => resilient.State == IntegrationConnectionState.Unavailable));

        // Server starts → connects and subscribes automatically.
        var server = NewServer(pipe);
        Assert.True(await WaitFor(() => resilient.IsConnected, 8000), "did not connect after the server started");
        Assert.True(await WaitFor(() => server.HasSubscriber(IntegrationEvents.ScreenshotCaptured)));
        server.Publish(IntegrationEvents.ScreenshotCaptured, new FileEventPayload { Path = "one" });
        Assert.True(await WaitFor(() => { lock (events) return events.Contains("one"); }));

        // Server restarts (e.g. DownloadDock restarted) → reconnects and re-subscribes.
        await server.DisposeAsync();
        Assert.True(await WaitFor(() => !resilient.IsConnected, 5000));
        await Assert.ThrowsAsync<IntegrationException>(() => resilient.SendAsync<PingResult>(IntegrationCommands.Ping));
        server = NewServer(pipe);
        Assert.True(await WaitFor(() => resilient.IsConnected && server.HasSubscriber(IntegrationEvents.ScreenshotCaptured), 8000), "did not reconnect");
        server.Publish(IntegrationEvents.ScreenshotCaptured, new FileEventPayload { Path = "two" });
        Assert.True(await WaitFor(() => { lock (events) return events.Contains("two"); }));
        Assert.NotNull(await resilient.SendAsync<PingResult>(IntegrationCommands.Ping));
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Resilient_client_reports_incompatible_servers()
    {
        var pipe = UniquePipe();
        // A "future" server that refuses our major version.
        var listener = Task.Run(async () =>
        {
            using var pipeServer = new NamedPipeServerStream(pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipeServer.WaitForConnectionAsync();
            var hello = await MessageFraming.ReadAsync(pipeServer, 65536, default);
            await MessageFraming.WriteAsync(pipeServer, IntegrationMessage.Failure(hello!.Id, IntegrationErrorCodes.IncompatibleVersion, "I speak 2.0"), 65536, default);
            await Task.Delay(500);
        });
        await using var resilient = new ResilientIntegrationClient(new IntegrationClientOptions { PipeName = pipe, ClientName = "x", ConnectTimeout = TimeSpan.FromSeconds(2) })
        {
            MinRetryDelay = TimeSpan.FromMilliseconds(100),
        };
        resilient.Start();
        Assert.True(await WaitFor(() => resilient.State == IntegrationConnectionState.Incompatible, 8000), $"state {resilient.State}");
        Assert.Contains("2.0", resilient.IncompatibleVersion);
        await listener;
    }

    [Fact]
    public async Task Nudge_reconnects_immediately_instead_of_waiting_for_backoff()
    {
        var pipe = UniquePipe();
        await using var resilient = new ResilientIntegrationClient(new IntegrationClientOptions { PipeName = pipe, ClientName = "x", ConnectTimeout = TimeSpan.FromMilliseconds(100) })
        {
            MinRetryDelay = TimeSpan.FromSeconds(30), // backoff alone would take ages
            MaxRetryDelay = TimeSpan.FromSeconds(30),
        };
        resilient.Start();
        Assert.True(await WaitFor(() => resilient.State == IntegrationConnectionState.Unavailable));
        await using var server = NewServer(pipe);
        await Task.Delay(300);
        resilient.Nudge();
        Assert.True(await WaitFor(() => resilient.IsConnected, 5000));
    }
}
