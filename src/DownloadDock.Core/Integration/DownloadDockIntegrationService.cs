using DownloadDock.Core.Downloads;
using DownloadDock.Integration;

namespace DownloadDock.Core.Integration;

/// <summary>What the integration server needs from the running application (implemented by the WPF app).</summary>
public interface IDownloadDockSurface
{
    /// <summary>Opens the existing fan (never a second one). Returns "opened" or "alreadyOpen".</summary>
    Task<string> ShowFanAsync(CancellationToken ct);

    /// <summary>Returns "closed" or "notOpen".</summary>
    Task<string> CloseFanAsync(CancellationToken ct);

    /// <summary>Opens (or focuses) the existing settings window. Returns "opened" or "alreadyOpen".</summary>
    Task<string> OpenSettingsAsync(CancellationToken ct);

    /// <summary>Thread-safe snapshot (no paths beyond the Downloads and screenshot folders).</summary>
    DownloadDockStatus GetStatus();
}

/// <summary>
/// DownloadDock's integration endpoint: serves downloadDock.* commands over the named pipe and publishes
/// events. Runs on background threads; every UI action goes through <see cref="IDownloadDockSurface"/>,
/// which marshals asynchronously to the dispatcher. No command accepts a path or can run anything.
/// </summary>
public sealed class DownloadDockIntegrationService : IAsyncDisposable
{
    public static readonly IReadOnlyList<string> ServedCapabilities = new[]
    {
        IntegrationCapabilities.ShowFan, IntegrationCapabilities.CloseFan, IntegrationCapabilities.OpenSettings,
        IntegrationCapabilities.GetStatus, IntegrationCapabilities.Events,
    };

    private readonly IDownloadDockSurface _surface;
    private readonly IntegrationServer _server;
    private readonly NewDownloadDetector _detector = new();
    private readonly Action<string>? _log;

    public DownloadDockIntegrationService(IDownloadDockSurface surface, string? appVersion, Action<string>? log = null, string? pipeName = null)
    {
        _surface = surface;
        _log = log;
        _server = new IntegrationServer(new IntegrationServerOptions
        {
            PipeName = pipeName ?? IntegrationEndpoints.DownloadDockPipeName(),
            ServerName = IntegrationProtocol.DownloadDockClientName,
            ServerVersion = appVersion,
            Capabilities = ServedCapabilities,
            Events = IntegrationEvents.All,
            MaxConnections = 8,
            RequestTimeout = TimeSpan.FromSeconds(10),
            Log = log is null ? null : m => log("Integration: " + m),
        });
        _server.Handle(IntegrationCommands.ShowFan, async ctx =>
        {
            var request = ctx.GetPayload<ShowFanRequest>();
            if (request.Anchor is not (null or "tray"))
                throw new IntegrationException(IntegrationErrorCodes.InvalidArgument, "Only the 'tray' anchor is supported.");
            return new ShowFanResult { State = await _surface.ShowFanAsync(ctx.CancellationToken).ConfigureAwait(false) };
        });
        _server.Handle(IntegrationCommands.CloseFan, async ctx => new CloseFanResult { State = await _surface.CloseFanAsync(ctx.CancellationToken).ConfigureAwait(false) });
        _server.Handle(IntegrationCommands.OpenSettings, async ctx => new OpenSettingsResult { State = await _surface.OpenSettingsAsync(ctx.CancellationToken).ConfigureAwait(false) });
        _server.Handle(IntegrationCommands.GetStatus, _ =>
        {
            var status = _surface.GetStatus();
            status.ProtocolVersion = IntegrationProtocol.Version;
            status.AppVersion ??= appVersion;
            status.State = "running";
            status.Capabilities = ServedCapabilities.ToList();
            status.Events = IntegrationEvents.All.ToList();
            return Task.FromResult<object?>(status);
        });
        _server.PeerConnected += peer =>
        {
            if (peer.ClientName == IntegrationProtocol.FinderClientName) FinderAnnounced?.Invoke(peer);
        };
    }

    /// <summary>Finder connected to us – a good moment for our client to (re)connect to Finder.</summary>
    public event Action<IntegrationPeer>? FinderAnnounced;

    public string PipeName => _server.PipeName;

    public bool IsListening => _server.IsListening;

    public string? StartupProblem => _server.StartupProblem;

    public IReadOnlyList<IntegrationPeer> Peers => _server.Peers;

    public void Start() => _server.Start();

    /// <summary>True when a connected Finder subscribed to the event (so a separate notification would be a duplicate).</summary>
    public bool FinderSubscribedTo(string eventName) => _server.HasSubscriber(eventName, IntegrationProtocol.FinderClientName);

    // ------------------------------------------------------------------ events

    /// <summary>
    /// Feed every Downloads snapshot (from the app's single existing watcher). Publishes
    /// downloadDetected for files that weren't there before; returns them so the caller can also
    /// notify Finder.
    /// </summary>
    public IReadOnlyList<DownloadEntry> OnDownloadsSnapshot(string folder, IReadOnlyList<DownloadEntry> items, Func<string, bool>? alreadyReported = null)
    {
        var added = _detector.Update(folder, items);
        // Files DownloadDock itself just put there (drops, screenshots) were already reported once.
        if (alreadyReported is not null) added = added.Where(e => !alreadyReported(e.FullPath)).ToList();
        foreach (var e in added)
            _server.Publish(IntegrationEvents.DownloadDetected, ToPayload(e.FullPath, e.Length, null));
        return added;
    }

    public void PublishScreenshotCaptured(string path, string source, long? size)
        => _server.Publish(IntegrationEvents.ScreenshotCaptured, ToPayload(path, size, source));

    public void PublishFileAddedToDownloads(string path, long? size)
        => _server.Publish(IntegrationEvents.FileAddedToDownloads, ToPayload(path, size, null));

    public void PublishDownloadsFolderChanged(string folder)
        => _server.Publish(IntegrationEvents.DownloadsFolderChanged, new FolderChangedPayload { Path = folder });

    public void PublishFanState(bool open)
        => _server.Publish(open ? IntegrationEvents.FanOpened : IntegrationEvents.FanClosed, new FanStatePayload { Open = open });

    private static FileEventPayload ToPayload(string path, long? size, string? source) => new()
    {
        Path = path,
        // Windows path rules regardless of the OS the code runs on (the protocol carries Windows paths).
        Name = path[(path.Replace('/', '\\').TrimEnd('\\').LastIndexOf('\\') + 1)..].TrimEnd('\\'),
        Folder = PathRules.ParentOf(path) ?? "",
        Size = size,
        Source = source,
    };

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// Turns successive Downloads snapshots into "new download" notifications without a second file watcher.
/// The first snapshot (and the first after a folder change) is the baseline and produces nothing.
/// </summary>
public sealed class NewDownloadDetector
{
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _folder;

    /// <summary>Returns entries whose path wasn't in the previous snapshot (renames of partial downloads count as new).</summary>
    public IReadOnlyList<DownloadEntry> Update(string folder, IReadOnlyList<DownloadEntry> items)
    {
        lock (_knownPaths)
        {
            var baseline = !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase);
            var added = baseline ? new List<DownloadEntry>() : items.Where(i => !_knownPaths.Contains(i.FullPath)).ToList();
            _folder = folder;
            _knownPaths.Clear();
            foreach (var i in items) _knownPaths.Add(i.FullPath);
            return added;
        }
    }
}
