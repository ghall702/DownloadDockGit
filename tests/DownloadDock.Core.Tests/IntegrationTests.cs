using System.Collections.Concurrent;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Integration;
using DownloadDock.Integration;

namespace DownloadDock.Core.Tests;

/// <summary>A scriptable OS environment: records Explorer fallbacks and Finder launches.</summary>
internal sealed class FakeEnvironment : IFinderEnvironment
{
    public AppRegistration? Registration { get; set; }
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> ExplorerOpened { get; } = new();
    public ConcurrentQueue<string> ExplorerRevealed { get; } = new();
    public ConcurrentQueue<(string Exe, string[] Args)> Launches { get; } = new();
    public bool LaunchSucceeds { get; set; } = true;
    public ConcurrentQueue<string> Logs { get; } = new();

    public AppRegistration? FinderRegistration() => Registration;
    public bool DirectoryExists(string path) => Directories.Contains(path.TrimEnd('\\')) || Directories.Contains(path);
    public bool FileExists(string path) => Files.Contains(path);
    public bool LaunchFinder(string executablePath, IReadOnlyList<string> arguments)
    {
        Launches.Enqueue((executablePath, arguments.ToArray()));
        return LaunchSucceeds;
    }
    public bool OpenInExplorer(string folder) { ExplorerOpened.Enqueue(folder); return true; }
    public bool RevealInExplorer(string path) { ExplorerRevealed.Enqueue(path); return true; }
    public void Log(string message) => Logs.Enqueue(message);
}

/// <summary>A mock "Finder for Windows" – a real integration server hosting the finder.* commands.</summary>
internal sealed class MockFinder : IAsyncDisposable
{
    public IntegrationServer Server { get; }
    public ConcurrentQueue<OpenFolderRequest> OpenedFolders { get; } = new();
    public ConcurrentQueue<RevealFileRequest> Revealed { get; } = new();
    public ConcurrentQueue<FilesReceivedRequest> Received { get; } = new();
    public string? FailWith { get; set; }
    public TimeSpan Delay { get; set; }

    public MockFinder(string pipe, bool supportsFilesReceived = true)
    {
        var caps = new List<string> { IntegrationCapabilities.OpenFolder, IntegrationCapabilities.RevealFile };
        if (supportsFilesReceived) caps.Add(IntegrationCapabilities.FilesReceived);
        Server = new IntegrationServer(new IntegrationServerOptions
        {
            PipeName = pipe, ServerName = IntegrationProtocol.FinderClientName, ServerVersion = "0.9.0", Capabilities = caps,
        });
        Server.Handle(IntegrationCommands.OpenFolder, async ctx =>
        {
            await Behave(ctx);
            var r = ctx.GetPayload<OpenFolderRequest>();
            if (PathRules.Validate(r.Path) is { } why) throw new IntegrationException(IntegrationErrorCodes.InvalidPath, why);
            OpenedFolders.Enqueue(r);
            return new FinderOpenResult { State = "opened" };
        });
        Server.Handle(IntegrationCommands.RevealFile, async ctx =>
        {
            await Behave(ctx);
            Revealed.Enqueue(ctx.GetPayload<RevealFileRequest>());
            return new FinderOpenResult { State = "selected" };
        });
        if (supportsFilesReceived)
        {
            Server.Handle(IntegrationCommands.FilesReceived, ctx =>
            {
                var r = ctx.GetPayload<FilesReceivedRequest>();
                Received.Enqueue(r);
                return Task.FromResult<object?>(new FilesReceivedResult { Accepted = r.Files.Count });
            });
        }
        Server.Start();
    }

    private async Task Behave(IntegrationRequestContext ctx)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ctx.CancellationToken);
        if (FailWith is { } code) throw new IntegrationException(code, "mock failure");
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

[Collection("Timing")]
public class FinderConnectorTests
{
    private const string Folder = @"C:\Users\Ana\Downloads";
    private const string File1 = @"C:\Users\Ana\Downloads\Project Brief.pdf";

    private static string Pipe() => "ddtest.finder." + Guid.NewGuid().ToString("N")[..10];

    private static async Task<bool> WaitFor(Func<bool> condition, int ms = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private static (FinderConnector Connector, FakeEnvironment Env) Create(string pipe, FinderIntegrationSettings? settings = null, Func<string, bool>? subscribed = null)
    {
        var env = new FakeEnvironment();
        env.Directories.Add(Folder);
        env.Files.Add(File1);
        var s = settings ?? new FinderIntegrationSettings();
        var connector = new FinderConnector(env, () => s, "1.0.0", subscribed, pipe, TimeSpan.FromMilliseconds(100));
        connector.Start();
        return (connector, env);
    }

    [Fact]
    public async Task Uses_a_running_finder_for_folders_and_reveal()
    {
        var pipe = Pipe();
        await using var finder = new MockFinder(pipe);
        var (connector, env) = Create(pipe);
        await using var _ = connector;
        Assert.True(await WaitFor(() => connector.IsConnected));
        Assert.Equal(FinderConnectionStatus.Connected, connector.Status.Status);
        Assert.Equal("0.9.0", connector.Status.FinderVersion);

        Assert.Equal(OpenOutcome.Finder, await connector.OpenFolderAsync(Folder, newWindow: true));
        Assert.True(finder.OpenedFolders.TryDequeue(out var opened));
        Assert.Equal(Folder, opened.Path);
        Assert.True(opened.NewWindow);

        Assert.Equal(OpenOutcome.Finder, await connector.RevealFileAsync(File1));
        Assert.True(finder.Revealed.TryDequeue(out var revealed));
        Assert.Equal(File1, revealed.Path);
        Assert.Empty(env.ExplorerOpened);
        Assert.Empty(env.ExplorerRevealed);
    }

    [Fact]
    public async Task Unicode_unc_and_mapped_drive_paths_reach_finder_intact()
    {
        var pipe = Pipe();
        await using var finder = new MockFinder(pipe);
        var (connector, env) = Create(pipe);
        await using var _ = connector;
        var paths = new[] { @"\\nas\共有\Fotos – Ünïcødé", @"Z:\Mapped Drive\Spaces In Name", @"G:\My Drive\Shared with me" };
        foreach (var p in paths) env.Directories.Add(p);
        Assert.True(await WaitFor(() => connector.IsConnected));
        foreach (var p in paths) Assert.Equal(OpenOutcome.Finder, await connector.OpenFolderAsync(p));
        Assert.Equal(paths, finder.OpenedFolders.Select(r => r.Path));
    }

    [Fact]
    public async Task Falls_back_to_explorer_when_finder_is_not_installed()
    {
        var (connector, env) = Create(Pipe());
        await using var _ = connector;
        Assert.Equal(FinderConnectionStatus.NotInstalled, connector.Status.Status);
        Assert.Equal(OpenOutcome.Explorer, await connector.OpenFolderAsync(Folder));
        Assert.Equal(OpenOutcome.Explorer, await connector.RevealFileAsync(File1));
        Assert.Equal(new[] { Folder }, env.ExplorerOpened);
        Assert.Equal(new[] { File1 }, env.ExplorerRevealed);
        Assert.Empty(env.Launches);
    }

    [Fact]
    public async Task Starts_an_installed_finder_that_is_not_running_with_fixed_arguments()
    {
        var (connector, env) = Create(Pipe());
        await using var _ = connector;
        env.Registration = new AppRegistration(@"C:\Program Files\Finder\Finder.exe", "0.9.0", "1.0", AllowLaunchByPeer: true);
        Assert.Equal(FinderConnectionStatus.NotRunning, connector.Status.Status);
        Assert.Equal(OpenOutcome.FinderLaunched, await connector.RevealFileAsync(File1));
        Assert.True(env.Launches.TryDequeue(out var launch));
        Assert.Equal(@"C:\Program Files\Finder\Finder.exe", launch.Exe);
        Assert.Equal(new[] { "--reveal", File1 }, launch.Args);
        Assert.Equal(OpenOutcome.FinderLaunched, await connector.OpenFolderAsync(Folder));
        Assert.True(env.Launches.TryDequeue(out var launch2));
        Assert.Equal(new[] { "--open-folder", Folder }, launch2.Args);
        Assert.Empty(env.ExplorerOpened);
    }

    [Fact]
    public async Task Never_starts_finder_when_the_user_or_finder_disallows_it_or_the_version_is_incompatible()
    {
        foreach (var (settings, reg) in new[]
        {
            (new FinderIntegrationSettings(LaunchFinderIfNotRunning: false), new AppRegistration(@"C:\F\Finder.exe", "1", "1.0", true)),
            (new FinderIntegrationSettings(), new AppRegistration(@"C:\F\Finder.exe", "1", "1.0", AllowLaunchByPeer: false)),
            (new FinderIntegrationSettings(), new AppRegistration(@"C:\F\Finder.exe", "2", "2.0", true)),
        })
        {
            var (connector, env) = Create(Pipe(), settings);
            env.Registration = reg;
            Assert.Equal(OpenOutcome.Explorer, await connector.OpenFolderAsync(Folder));
            Assert.Empty(env.Launches);
            await connector.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_launch_falls_back_to_explorer()
    {
        var (connector, env) = Create(Pipe());
        await using var _ = connector;
        env.Registration = new AppRegistration(@"C:\F\Finder.exe", "1", "1.0", true);
        env.LaunchSucceeds = false;
        Assert.Equal(OpenOutcome.Explorer, await connector.RevealFileAsync(File1));
        Assert.Single(env.ExplorerRevealed);
    }

    [Fact]
    public async Task Finder_errors_and_hangs_fall_back_to_explorer()
    {
        var pipe = Pipe();
        await using var finder = new MockFinder(pipe) { FailWith = IntegrationErrorCodes.InternalError };
        var (connector, env) = Create(pipe);
        await using var _ = connector;
        Assert.True(await WaitFor(() => connector.IsConnected));
        Assert.Equal(OpenOutcome.Explorer, await connector.OpenFolderAsync(Folder));

        finder.FailWith = null;
        finder.Delay = TimeSpan.FromSeconds(8); // Finder frozen
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(OpenOutcome.Explorer, await connector.RevealFileAsync(File1));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"fallback took {sw.Elapsed}");
        Assert.Single(env.ExplorerOpened);
        Assert.Single(env.ExplorerRevealed);
    }

    [Fact]
    public async Task Preferences_route_each_action_independently()
    {
        var pipe = Pipe();
        await using var finder = new MockFinder(pipe);
        var (connector, env) = Create(pipe, new FinderIntegrationSettings(PreferFinderForFolders: true, PreferFinderForReveal: false));
        await using var _ = connector;
        Assert.True(await WaitFor(() => connector.IsConnected));
        Assert.Equal(OpenOutcome.Finder, await connector.OpenFolderAsync(Folder));
        Assert.Equal(OpenOutcome.Explorer, await connector.RevealFileAsync(File1));
        Assert.False(connector.WouldUseFinderForReveal);
    }

    [Fact]
    public async Task Disabled_integration_never_connects_and_uses_explorer()
    {
        var pipe = Pipe();
        await using var finder = new MockFinder(pipe);
        var (connector, env) = Create(pipe, new FinderIntegrationSettings(Enabled: false));
        await using var _ = connector;
        await Task.Delay(500);
        Assert.False(connector.IsConnected);
        Assert.Empty(finder.Server.Peers);
        Assert.Equal(FinderConnectionStatus.Disabled, connector.Status.Status);
        Assert.Equal(OpenOutcome.Explorer, await connector.OpenFolderAsync(Folder));
        Assert.Empty(finder.OpenedFolders);
    }

    [Fact]
    public async Task Missing_items_invalid_paths_and_offline_drives_are_handled()
    {
        var (connector, env) = Create(Pipe());
        await using var _ = connector;
        // File gone, folder still there → open the folder.
        Assert.Equal(OpenOutcome.ParentOpened, await connector.RevealFileAsync(@"C:\Users\Ana\Downloads\deleted.pdf"));
        Assert.Equal(new[] { Folder }, env.ExplorerOpened);
        // Whole drive offline / share unreachable → nothing opened, no exception.
        Assert.Equal(OpenOutcome.NotFound, await connector.RevealFileAsync(@"\\offline\share\file.pdf"));
        Assert.Equal(OpenOutcome.NotFound, await connector.OpenFolderAsync(@"X:\Gone"));
        // Unsafe input is refused.
        Assert.Equal(OpenOutcome.InvalidPath, await connector.OpenFolderAsync(@"..\..\Windows"));
        Assert.Equal(OpenOutcome.InvalidPath, await connector.RevealFileAsync(@"\\.\PhysicalDrive0"));
        Assert.Single(env.ExplorerOpened);
        Assert.Empty(env.ExplorerRevealed);
    }

    [Fact]
    public async Task Reconnects_when_finder_restarts_and_when_finder_starts_after_downloaddock()
    {
        var pipe = Pipe();
        var (connector, env) = Create(pipe);
        await using var _ = connector;
        Assert.False(connector.IsConnected);
        Assert.Equal(OpenOutcome.Explorer, await connector.OpenFolderAsync(Folder)); // Finder not running yet

        var finder = new MockFinder(pipe);
        connector.Nudge(); // what happens when Finder connects to DownloadDock's server
        Assert.True(await WaitFor(() => connector.IsConnected), "did not connect after Finder started");
        Assert.Equal(OpenOutcome.Finder, await connector.OpenFolderAsync(Folder));

        await finder.DisposeAsync(); // Finder crashes / updates
        Assert.True(await WaitFor(() => !connector.IsConnected));
        Assert.Equal(OpenOutcome.Explorer, await connector.OpenFolderAsync(Folder)); // still works meanwhile

        finder = new MockFinder(pipe); // Finder comes back
        Assert.True(await WaitFor(() => connector.IsConnected), "did not reconnect after Finder restarted");
        Assert.Equal(OpenOutcome.Finder, await connector.RevealFileAsync(File1));
        await finder.DisposeAsync();
    }

    [Fact]
    public async Task Files_received_notifications_are_batched_and_skipped_when_finder_subscribes_to_events()
    {
        var pipe = Pipe();
        await using var finder = new MockFinder(pipe);
        var subscribed = new HashSet<string>();
        var (connector, env) = Create(pipe, subscribed: e => subscribed.Contains(e));
        await using var _ = connector;
        Assert.True(await WaitFor(() => connector.IsConnected));

        connector.NotifyFileReceived(@"C:\Pics\Screenshot 1.png", ReceivedFileKinds.Screenshot, 10);
        connector.NotifyFileReceived(@"C:\Users\Ana\Downloads\a.pdf", ReceivedFileKinds.Download, 20);
        connector.NotifyFileReceived(@"C:\Users\Ana\Downloads\b.png", ReceivedFileKinds.AddedToDownloads);
        Assert.True(await WaitFor(() => !finder.Received.IsEmpty));
        await Task.Delay(500);
        var batch = Assert.Single(finder.Received);
        Assert.Equal(3, batch.Files.Count);
        Assert.True(batch.Highlight);
        Assert.Equal(ReceivedFileKinds.Screenshot, batch.Files[0].Kind);

        // Finder subscribed to screenshot events → no duplicate filesReceived for screenshots.
        subscribed.Add(IntegrationEvents.ScreenshotCaptured);
        connector.NotifyFileReceived(@"C:\Pics\Screenshot 2.png", ReceivedFileKinds.Screenshot);
        await Task.Delay(800);
        Assert.Single(finder.Received);

        // Invalid paths are never sent.
        connector.NotifyFileReceived(@"relative\x.png", ReceivedFileKinds.Download);
        await Task.Delay(800);
        Assert.Single(finder.Received);
    }

    [Fact]
    public async Task No_notifications_without_a_connected_finder_that_supports_them()
    {
        var pipe = Pipe();
        await using var finder = new MockFinder(pipe, supportsFilesReceived: false);
        var (connector, env) = Create(pipe);
        await using var _ = connector;
        Assert.True(await WaitFor(() => connector.IsConnected));
        connector.NotifyFileReceived(@"C:\Pics\s.png", ReceivedFileKinds.Screenshot);
        await Task.Delay(800);
        Assert.Empty(finder.Received);
        Assert.DoesNotContain(env.Logs, l => l.Contains("unknownCommand"));
    }
}

/// <summary>A fake app surface recording what Finder asked for.</summary>
internal sealed class FakeSurface : IDownloadDockSurface
{
    public int ShowFanCalls, CloseFanCalls, SettingsCalls;
    public bool FanOpen;

    public Task<string> ShowFanAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ShowFanCalls);
        var state = FanOpen ? "alreadyOpen" : "opened";
        FanOpen = true;
        return Task.FromResult(state);
    }

    public Task<string> CloseFanAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref CloseFanCalls);
        var state = FanOpen ? "closed" : "notOpen";
        FanOpen = false;
        return Task.FromResult(state);
    }

    public Task<string> OpenSettingsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref SettingsCalls);
        return Task.FromResult("opened");
    }

    public DownloadDockStatus GetStatus() => new()
    {
        DownloadsFolder = @"C:\Users\Ana\Downloads",
        ScreenshotFolder = @"C:\Users\Ana\Pictures\Screenshots",
        ScreenshotsEnabled = true,
        FanOpen = FanOpen,
    };
}

[Collection("Timing")]
public class DownloadDockIntegrationServiceTests
{
    private static async Task<IntegrationClient> FinderClient(string pipe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var c = new IntegrationClient(new IntegrationClientOptions { PipeName = pipe, ClientName = IntegrationProtocol.FinderClientName, ClientVersion = "0.9.0" });
            try { await c.ConnectAsync(); return c; }
            catch (IntegrationException) when (DateTime.UtcNow < deadline) { await c.DisposeAsync(); await Task.Delay(50); }
        }
    }

    [Fact]
    public async Task Finder_can_show_the_fan_open_settings_and_query_status()
    {
        var pipe = "ddtest.dd." + Guid.NewGuid().ToString("N")[..10];
        var surface = new FakeSurface();
        await using var service = new DownloadDockIntegrationService(surface, "1.4.0", null, pipe);
        IntegrationPeer? announced = null;
        service.FinderAnnounced += p => announced = p;
        service.Start();
        await using var finder = await FinderClient(pipe);

        Assert.Equal("DownloadDock", finder.Server!.ServerName);
        Assert.Contains(IntegrationCapabilities.ShowFan, finder.Server.Capabilities);
        Assert.NotNull(announced);

        Assert.Equal("opened", (await finder.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan))!.State);
        Assert.Equal("alreadyOpen", (await finder.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan))!.State); // never a duplicate fan
        Assert.Equal(2, surface.ShowFanCalls);
        Assert.Equal("closed", (await finder.SendAsync<CloseFanResult>(IntegrationCommands.CloseFan))!.State);
        Assert.Equal("opened", (await finder.SendAsync<OpenSettingsResult>(IntegrationCommands.OpenSettings))!.State);

        var status = (await finder.SendAsync<DownloadDockStatus>(IntegrationCommands.GetStatus))!;
        Assert.Equal(IntegrationProtocol.Version, status.ProtocolVersion);
        Assert.Equal("1.4.0", status.AppVersion);
        Assert.Equal("running", status.State);
        Assert.Equal(@"C:\Users\Ana\Downloads", status.DownloadsFolder);
        Assert.Equal(@"C:\Users\Ana\Pictures\Screenshots", status.ScreenshotFolder);
        Assert.False(status.FanOpen);
        Assert.Contains(IntegrationCapabilities.OpenSettings, status.Capabilities);
        Assert.Equal(IntegrationEvents.All, status.Events);

        var bad = await Assert.ThrowsAsync<IntegrationException>(() => finder.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan, new ShowFanRequest { Anchor = "desktop" }));
        Assert.Equal(IntegrationErrorCodes.InvalidArgument, bad.Code);
        Assert.Equal(IntegrationErrorCodes.UnknownCommand, (await Assert.ThrowsAsync<IntegrationException>(() => finder.SendAsync<object>("downloadDock.deleteEverything"))).Code);
    }

    [Fact]
    public async Task Finder_receives_subscribed_events_including_new_downloads_from_snapshots()
    {
        var pipe = "ddtest.dd." + Guid.NewGuid().ToString("N")[..10];
        await using var service = new DownloadDockIntegrationService(new FakeSurface(), "1.0", null, pipe);
        service.Start();
        await using var finder = await FinderClient(pipe);
        var events = new ConcurrentQueue<IntegrationEvent>();
        finder.EventReceived += events.Enqueue;
        await finder.SubscribeAsync(IntegrationEvents.All);
        Assert.True(service.FinderSubscribedTo(IntegrationEvents.ScreenshotCaptured));

        var t = DateTime.UtcNow;
        var a = new DownloadEntry(@"C:\D\a.pdf", "a.pdf", 1, t, t);
        var b = new DownloadEntry(@"C:\D\b.zip", "b.zip", 2, t, t);
        Assert.Empty(service.OnDownloadsSnapshot(@"C:\D", new[] { a }));            // baseline: nothing
        Assert.Single(service.OnDownloadsSnapshot(@"C:\D", new[] { b, a }));        // b is new
        Assert.Empty(service.OnDownloadsSnapshot(@"C:\D", new[] { b with { Length = 9 }, a })); // grew, not new
        service.PublishScreenshotCaptured(@"C:\P\Screenshot 2026-11-09 08-30-15.png", "region", 1234);
        service.PublishFileAddedToDownloads(@"C:\D\dropped.png", 55);
        service.PublishDownloadsFolderChanged(@"E:\Downloads");
        service.PublishFanState(true);
        service.PublishFanState(false);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (events.Count < 6 && DateTime.UtcNow < deadline) await Task.Delay(20);
        var list = events.ToList();
        Assert.Equal(new[]
        {
            IntegrationEvents.DownloadDetected, IntegrationEvents.ScreenshotCaptured, IntegrationEvents.FileAddedToDownloads,
            IntegrationEvents.DownloadsFolderChanged, IntegrationEvents.FanOpened, IntegrationEvents.FanClosed,
        }, list.Select(e => e.Name));
        var download = list[0].GetPayload<FileEventPayload>()!;
        Assert.Equal(@"C:\D\b.zip", download.Path);
        Assert.Equal("b.zip", download.Name);
        Assert.Equal("region", list[1].GetPayload<FileEventPayload>()!.Source);
        Assert.Equal(@"E:\Downloads", list[3].GetPayload<FolderChangedPayload>()!.Path);
        Assert.True(list[4].GetPayload<FanStatePayload>()!.Open);
    }

    [Fact]
    public void Download_detector_resets_on_folder_change()
    {
        var d = new NewDownloadDetector();
        var t = DateTime.UtcNow;
        var a = new DownloadEntry(@"C:\D\a.pdf", "a.pdf", 1, t, t);
        var x = new DownloadEntry(@"E:\X\x.pdf", "x.pdf", 1, t, t);
        Assert.Empty(d.Update(@"C:\D", new[] { a }));
        Assert.Empty(d.Update(@"E:\X", new[] { x })); // new folder = new baseline
        Assert.Empty(d.Update(@"E:\X", new[] { x }));
        Assert.Single(d.Update(@"E:\X", new[] { x, a }));
    }
}
