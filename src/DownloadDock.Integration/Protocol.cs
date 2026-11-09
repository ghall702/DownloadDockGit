namespace DownloadDock.Integration;

/// <summary>Protocol version, limits and endpoint names. See docs/FINDER_INTEGRATION.md.</summary>
public static class IntegrationProtocol
{
    /// <summary>Major version: changes only for incompatible protocol changes (and appears in the pipe names).</summary>
    public const int MajorVersion = 1;

    /// <summary>Minor version: additive, backwards-compatible changes (new optional fields, commands, events).</summary>
    public const int MinorVersion = 0;

    public static string Version => $"{MajorVersion}.{MinorVersion}";

    /// <summary>Largest accepted message (UTF-8 JSON body), in bytes. Larger messages close the connection.</summary>
    public const int DefaultMaxMessageBytes = 64 * 1024;

    /// <summary>Hard ceiling for <see cref="DefaultMaxMessageBytes"/> overrides.</summary>
    public const int AbsoluteMaxMessageBytes = 1024 * 1024;

    /// <summary>Most files allowed in one finder.filesReceived request.</summary>
    public const int MaxFilesPerNotification = 100;

    /// <summary>Longest path accepted (Windows extended-length limit).</summary>
    public const int MaxPathLength = 32767;

    /// <summary>Client display names used in hello (identification only – NOT authentication).</summary>
    public const string DownloadDockClientName = "DownloadDock";
    public const string FinderClientName = "FinderForWindows";
}

/// <summary>Command names (requests). Names are case-sensitive.</summary>
public static class IntegrationCommands
{
    // Built into every endpoint.
    public const string Hello = "system.hello";
    public const string Ping = "system.ping";
    public const string Subscribe = "system.subscribe";
    public const string Unsubscribe = "system.unsubscribe";

    // Served by DownloadDock.
    public const string ShowFan = "downloadDock.showFan";
    public const string CloseFan = "downloadDock.closeFan";
    public const string OpenSettings = "downloadDock.openSettings";
    public const string GetStatus = "downloadDock.getStatus";

    // Served by Finder for Windows.
    public const string OpenFolder = "finder.openFolder";
    public const string RevealFile = "finder.revealFile";
    public const string FilesReceived = "finder.filesReceived";
}

/// <summary>Event names published by DownloadDock to subscribers.</summary>
public static class IntegrationEvents
{
    public const string DownloadDetected = "downloadDock.downloadDetected";
    public const string ScreenshotCaptured = "downloadDock.screenshotCaptured";
    public const string FileAddedToDownloads = "downloadDock.fileAddedToDownloads";
    public const string DownloadsFolderChanged = "downloadDock.downloadsFolderChanged";
    public const string FanOpened = "downloadDock.fanOpened";
    public const string FanClosed = "downloadDock.fanClosed";

    public static readonly IReadOnlyList<string> All = new[]
    {
        DownloadDetected, ScreenshotCaptured, FileAddedToDownloads, DownloadsFolderChanged, FanOpened, FanClosed,
    };
}

/// <summary>Capability identifiers exchanged in hello. A peer must only call commands the other side lists.</summary>
public static class IntegrationCapabilities
{
    public const string ShowFan = IntegrationCommands.ShowFan;
    public const string CloseFan = IntegrationCommands.CloseFan;
    public const string OpenSettings = IntegrationCommands.OpenSettings;
    public const string GetStatus = IntegrationCommands.GetStatus;
    public const string Events = "events";
    public const string OpenFolder = IntegrationCommands.OpenFolder;
    public const string RevealFile = IntegrationCommands.RevealFile;
    public const string FilesReceived = IntegrationCommands.FilesReceived;
}

/// <summary>Stable error codes carried in error responses.</summary>
public static class IntegrationErrorCodes
{
    public const string InvalidMessage = "invalidMessage";
    public const string MessageTooLarge = "messageTooLarge";
    public const string HandshakeRequired = "handshakeRequired";
    public const string IncompatibleVersion = "incompatibleVersion";
    public const string UnknownCommand = "unknownCommand";
    public const string InvalidArgument = "invalidArgument";
    public const string InvalidPath = "invalidPath";
    public const string NotFound = "notFound";
    public const string Unavailable = "unavailable";
    public const string AccessDenied = "accessDenied";
    public const string Busy = "busy";
    public const string Timeout = "timeout";
    public const string Disconnected = "disconnected";
    public const string Disabled = "disabled";
    public const string InternalError = "internalError";
}
