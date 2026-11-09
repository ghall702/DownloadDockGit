using DownloadDock.Integration;

namespace DownloadDock.Core.Integration;

/// <summary>Everything the connector needs from the OS (implemented by the app; faked in tests).</summary>
public interface IFinderEnvironment
{
    /// <summary>Finder's self-registration (HKCU/HKLM), or null when Finder isn't installed.</summary>
    AppRegistration? FinderRegistration();

    bool DirectoryExists(string path);
    bool FileExists(string path);

    /// <summary>Starts the registered Finder executable with fixed, documented arguments. Never throws.</summary>
    bool LaunchFinder(string executablePath, IReadOnlyList<string> arguments);

    /// <summary>Opens a folder in Windows File Explorer. Never throws.</summary>
    bool OpenInExplorer(string folder);

    /// <summary>Opens the containing folder in File Explorer with the item selected. Never throws.</summary>
    bool RevealInExplorer(string path);

    void Log(string message);
}

/// <summary>User preferences for the Finder integration.</summary>
public sealed record FinderIntegrationSettings(
    bool Enabled = true,
    bool PreferFinderForFolders = true,
    bool PreferFinderForReveal = true,
    bool LaunchFinderIfNotRunning = true);

public enum FinderConnectionStatus
{
    Disabled,
    NotInstalled,
    NotRunning,
    Connecting,
    Connected,
    Incompatible,
}

public sealed record FinderStatus(FinderConnectionStatus Status, string? FinderVersion, string? ProtocolVersion, string Description);

/// <summary>Who ended up handling an open/reveal request.</summary>
public enum OpenOutcome
{
    /// <summary>A running Finder handled it.</summary>
    Finder,
    /// <summary>Finder wasn't running; the registered Finder was started with the request.</summary>
    FinderLaunched,
    /// <summary>File Explorer handled it (preference, Finder unavailable, or Finder failed).</summary>
    Explorer,
    /// <summary>A file was missing, so its containing folder was opened instead.</summary>
    ParentOpened,
    /// <summary>Nothing to open: the folder/file and its parent don't exist (or the drive is offline).</summary>
    NotFound,
    /// <summary>The path was rejected by validation.</summary>
    InvalidPath,
    /// <summary>Opening failed everywhere (Explorer could not start).</summary>
    Failed,
}

/// <summary>
/// DownloadDock's side of the Finder integration: keeps a resilient connection to Finder's pipe, routes
/// "open folder" / "show in folder" to Finder when preferred and available, and ALWAYS falls back to
/// File Explorer – the user never loses the ability to open a containing folder. Also batches
/// finder.filesReceived notifications, skipping ones Finder already gets through event subscriptions.
/// </summary>
public sealed class FinderConnector : IAsyncDisposable
{
    public static readonly TimeSpan FinderRequestTimeout = TimeSpan.FromSeconds(3);
    private const int MaxQueuedNotifications = IntegrationProtocol.MaxFilesPerNotification;

    private readonly IFinderEnvironment _env;
    private readonly Func<FinderIntegrationSettings> _settings;
    private readonly Func<string, bool> _finderSubscribedTo;
    private readonly ResilientIntegrationClient _client;
    private readonly object _batchGate = new();
    private readonly List<ReceivedFile> _batch = new();
    private readonly Timer _flushTimer;
    private bool _started;

    public FinderConnector(IFinderEnvironment env, Func<FinderIntegrationSettings> settings, string? appVersion,
        Func<string, bool>? finderSubscribedTo = null, string? finderPipeName = null, TimeSpan? retryDelay = null)
    {
        _env = env;
        _settings = settings;
        _finderSubscribedTo = finderSubscribedTo ?? (_ => false);
        _client = new ResilientIntegrationClient(new IntegrationClientOptions
        {
            PipeName = finderPipeName ?? IntegrationEndpoints.FinderPipeName(),
            ClientName = IntegrationProtocol.DownloadDockClientName,
            ClientVersion = appVersion,
            Capabilities = DownloadDockIntegrationService.ServedCapabilities,
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            RequestTimeout = FinderRequestTimeout,
            Log = m => env.Log("Finder link: " + m),
        })
        {
            MinRetryDelay = retryDelay ?? TimeSpan.FromSeconds(2),
            MaxRetryDelay = retryDelay is { } d ? TimeSpan.FromTicks(d.Ticks * 4) : TimeSpan.FromSeconds(30),
        };
        _client.StateChanged += _ => StatusChanged?.Invoke(Status);
        _flushTimer = new Timer(_ => _ = FlushNotificationsAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised (background thread) when the Finder link changes state.</summary>
    public event Action<FinderStatus>? StatusChanged;

    /// <summary>Begins connecting in the background (only if the integration is enabled).</summary>
    public void Start()
    {
        if (_started || !_settings().Enabled) return;
        _started = true;
        _client.Start();
    }

    /// <summary>Applies changed settings (enable/disable).</summary>
    public void SettingsChanged()
    {
        if (_settings().Enabled) Start();
        StatusChanged?.Invoke(Status);
    }

    /// <summary>Finder just connected to us: try connecting to it right away.</summary>
    public void Nudge()
    {
        if (!_started) Start();
        _client.Nudge();
    }

    public bool IsConnected => _settings().Enabled && _client.IsConnected;

    public FinderStatus Status
    {
        get
        {
            if (!_settings().Enabled) return new(FinderConnectionStatus.Disabled, null, null, "Finder integration is off.");
            if (_client.IsConnected && _client.Server is { } s)
                return new(FinderConnectionStatus.Connected, s.ServerVersion, s.ProtocolVersion, $"Connected to Finder {s.ServerVersion} (protocol {s.ProtocolVersion}).");
            var reg = SafeRegistration();
            if (_client.State == IntegrationConnectionState.Incompatible)
                return new(FinderConnectionStatus.Incompatible, reg?.Version, null, "Finder is running an incompatible integration version; File Explorer is used.");
            if (reg is null) return new(FinderConnectionStatus.NotInstalled, null, null, "Finder for Windows is not installed; File Explorer is used.");
            return new(FinderConnectionStatus.NotRunning, reg.Version, reg.ProtocolVersion,
                $"Finder {reg.Version} is installed but not running" + (_settings().LaunchFinderIfNotRunning ? "; it will be started when needed." : "; File Explorer is used."));
        }
    }

    /// <summary>Would "Show in folder" go to Finder right now (for menu labels)?</summary>
    public bool WouldUseFinderForReveal
    {
        get
        {
            var s = _settings();
            if (!s.Enabled || !s.PreferFinderForReveal) return false;
            if (_client.IsConnected && _client.Supports(IntegrationCapabilities.RevealFile)) return true;
            return s.LaunchFinderIfNotRunning && CanLaunch(SafeRegistration());
        }
    }

    // ------------------------------------------------------------------ open / reveal

    public async Task<OpenOutcome> OpenFolderAsync(string path, bool newWindow = false)
    {
        if (PathRules.Validate(path) is { } why)
        {
            _env.Log($"Open folder refused ({why})");
            return OpenOutcome.InvalidPath;
        }
        if (!_env.DirectoryExists(path))
        {
            _env.Log("Open folder: folder not found or drive unavailable");
            return OpenOutcome.NotFound;
        }

        var s = _settings();
        if (s.Enabled && s.PreferFinderForFolders)
        {
            var viaFinder = await TryFinderAsync(IntegrationCommands.OpenFolder, new OpenFolderRequest { Path = path, NewWindow = newWindow },
                "--open-folder", path).ConfigureAwait(false);
            if (viaFinder is { } outcome) return outcome;
        }
        return _env.OpenInExplorer(path) ? OpenOutcome.Explorer : OpenOutcome.Failed;
    }

    public async Task<OpenOutcome> RevealFileAsync(string path, bool newWindow = false)
    {
        if (PathRules.Validate(path) is { } why)
        {
            _env.Log($"Reveal refused ({why})");
            return OpenOutcome.InvalidPath;
        }
        if (!_env.FileExists(path) && !_env.DirectoryExists(path))
        {
            // The file is gone (moved/deleted) – open its folder if that still exists.
            var parent = PathRules.ParentOf(path);
            if (parent is not null && _env.DirectoryExists(parent))
            {
                var opened = await OpenFolderAsync(parent).ConfigureAwait(false);
                return opened is OpenOutcome.Failed or OpenOutcome.InvalidPath or OpenOutcome.NotFound ? opened : OpenOutcome.ParentOpened;
            }
            _env.Log("Reveal: file and folder not found or drive unavailable");
            return OpenOutcome.NotFound;
        }

        var s = _settings();
        if (s.Enabled && s.PreferFinderForReveal)
        {
            var viaFinder = await TryFinderAsync(IntegrationCommands.RevealFile, new RevealFileRequest { Path = path, NewWindow = newWindow },
                "--reveal", path).ConfigureAwait(false);
            if (viaFinder is { } outcome) return outcome;
        }
        return _env.RevealInExplorer(path) ? OpenOutcome.Explorer : OpenOutcome.Failed;
    }

    /// <summary>Asks a running Finder, or starts the registered Finder; null means "use Explorer".</summary>
    private async Task<OpenOutcome?> TryFinderAsync(string command, object payload, string launchSwitch, string path)
    {
        if (_client.IsConnected && _client.Supports(command))
        {
            try
            {
                await _client.SendAsync<FinderOpenResult>(command, payload, FinderRequestTimeout).ConfigureAwait(false);
                return OpenOutcome.Finder;
            }
            catch (IntegrationException ex)
            {
                _env.Log($"Finder could not handle {command} ({ex.Code}); using File Explorer");
                return null;
            }
        }

        var s = _settings();
        var reg = SafeRegistration();
        if (!_client.IsConnected && s.LaunchFinderIfNotRunning && CanLaunch(reg))
        {
            if (_env.LaunchFinder(reg!.ExecutablePath, new[] { launchSwitch, path }))
            {
                _env.Log("Started Finder for " + command);
                _client.Nudge();
                return OpenOutcome.FinderLaunched;
            }
            _env.Log("Finder could not be started; using File Explorer");
        }
        return null;
    }

    /// <summary>Only a registered, existing, protocol-compatible Finder whose user allows launching is started.</summary>
    private static bool CanLaunch(AppRegistration? reg)
        => reg is not null && reg.AllowLaunchByPeer && VersionNegotiation.Negotiate(reg.ProtocolVersion ?? IntegrationProtocol.Version) is not null;

    private AppRegistration? SafeRegistration()
    {
        try { return _env.FinderRegistration(); }
        catch (Exception ex) { _env.Log("Reading Finder registration failed: " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ notifications

    /// <summary>
    /// Queues a finder.filesReceived notification (batched for 400 ms). Skipped when Finder isn't
    /// connected, doesn't support it, or already subscribed to the equivalent DownloadDock event.
    /// </summary>
    public void NotifyFileReceived(string path, string kind, long? size = null)
    {
        if (!IsConnected || !_client.Supports(IntegrationCapabilities.FilesReceived)) return;
        if (_finderSubscribedTo(EventFor(kind))) return;
        if (PathRules.Validate(path) is not null) return;
        lock (_batchGate)
        {
            if (_batch.Count >= MaxQueuedNotifications) _batch.RemoveAt(0);
            _batch.Add(new ReceivedFile { Path = path, Kind = kind, Size = size });
            _flushTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        }
    }

    public static string EventFor(string kind) => kind switch
    {
        ReceivedFileKinds.Screenshot => IntegrationEvents.ScreenshotCaptured,
        ReceivedFileKinds.AddedToDownloads => IntegrationEvents.FileAddedToDownloads,
        _ => IntegrationEvents.DownloadDetected,
    };

    internal async Task FlushNotificationsAsync()
    {
        List<ReceivedFile> files;
        lock (_batchGate)
        {
            if (_batch.Count == 0) return;
            files = _batch.ToList();
            _batch.Clear();
        }
        try
        {
            await _client.SendAsync<FilesReceivedResult>(IntegrationCommands.FilesReceived,
                new FilesReceivedRequest { Files = files, Highlight = files.Any(f => f.Kind != ReceivedFileKinds.Download) },
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (IntegrationException ex)
        {
            _env.Log($"finder.filesReceived not delivered ({ex.Code})");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _flushTimer.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
