using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadDock.Integration;

/// <summary>Message kinds on the wire.</summary>
public static class MessageTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

/// <summary>
/// The single JSON envelope used for every message:
/// requests {type,id,command,payload}, responses {type,id,ok,result|error}, events {type,event,seq,payload}.
/// </summary>
public sealed class IntegrationMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("seq")] public long? Seq { get; set; }
    [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("result")] public JsonElement? Result { get; set; }
    [JsonPropertyName("error")] public IntegrationError? Error { get; set; }

    public static IntegrationMessage Request(string id, string command, object? payload) => new()
    {
        Type = MessageTypes.Request, Id = id, Command = command, Payload = IntegrationJson.ToElement(payload),
    };

    public static IntegrationMessage Success(string id, object? result) => new()
    {
        Type = MessageTypes.Response, Id = id, Ok = true, Result = IntegrationJson.ToElement(result),
    };

    public static IntegrationMessage Failure(string? id, string code, string message) => new()
    {
        Type = MessageTypes.Response, Id = id, Ok = false, Error = new IntegrationError { Code = code, Message = message },
    };

    public static IntegrationMessage EventMessage(string name, long seq, object? payload) => new()
    {
        Type = MessageTypes.Event, Event = name, Seq = seq, Payload = IntegrationJson.ToElement(payload),
    };
}

public sealed class IntegrationError
{
    [JsonPropertyName("code")] public string Code { get; set; } = IntegrationErrorCodes.InternalError;
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary>Raised by clients for error responses, timeouts and disconnections; and by handlers to return a specific error.</summary>
public sealed class IntegrationException : Exception
{
    public IntegrationException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;

    /// <summary>One of <see cref="IntegrationErrorCodes"/>.</summary>
    public string Code { get; }
}

// ------------------------------------------------------------------ system payloads

public sealed class HelloRequest
{
    /// <summary>"major.minor" of the protocol the caller speaks.</summary>
    public string ProtocolVersion { get; set; } = IntegrationProtocol.Version;
    public string ClientName { get; set; } = "";
    public string? ClientVersion { get; set; }
    public List<string> Capabilities { get; set; } = new();
}

public sealed class HelloResult
{
    /// <summary>Negotiated version (same major; the lower minor of the two).</summary>
    public string ProtocolVersion { get; set; } = IntegrationProtocol.Version;
    public string ServerName { get; set; } = "";
    public string? ServerVersion { get; set; }
    public List<string> Capabilities { get; set; } = new();
    public List<string> Events { get; set; } = new();

    /// <summary>Server process id – e.g. for AllowSetForegroundWindow before asking it to show UI.</summary>
    public int ProcessId { get; set; }

    public int MaxMessageBytes { get; set; } = IntegrationProtocol.DefaultMaxMessageBytes;
}

public sealed class SubscribeRequest
{
    public List<string> Events { get; set; } = new();
}

public sealed class SubscribeResult
{
    public List<string> Subscribed { get; set; } = new();
    public List<string> Unknown { get; set; } = new();
}

public sealed class PingResult
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
}

// ------------------------------------------------------------------ DownloadDock commands

public sealed class ShowFanRequest
{
    /// <summary>Reserved for future anchors; only "tray" (default) is supported.</summary>
    public string? Anchor { get; set; }
}

public sealed class ShowFanResult
{
    /// <summary>"opened" or "alreadyOpen".</summary>
    public string State { get; set; } = "opened";
}

public sealed class CloseFanResult
{
    /// <summary>"closed" or "notOpen".</summary>
    public string State { get; set; } = "closed";
}

public sealed class OpenSettingsResult
{
    /// <summary>"opened" or "alreadyOpen".</summary>
    public string State { get; set; } = "opened";
}

public sealed class DownloadDockStatus
{
    public string ProtocolVersion { get; set; } = IntegrationProtocol.Version;
    public string? AppVersion { get; set; }
    /// <summary>Always "running" when answered (the process is alive and serving requests).</summary>
    public string State { get; set; } = "running";
    public string? DownloadsFolder { get; set; }
    public string? ScreenshotFolder { get; set; }
    public bool ScreenshotsEnabled { get; set; }
    public bool? FanOpen { get; set; }
    public List<string> Capabilities { get; set; } = new();
    public List<string> Events { get; set; } = new();
}

// ------------------------------------------------------------------ Finder commands

public sealed class OpenFolderRequest
{
    /// <summary>Absolute folder path (drive, UNC or mapped drive).</summary>
    public string Path { get; set; } = "";

    /// <summary>True = always open a new window; false = reuse an existing window where sensible.</summary>
    public bool NewWindow { get; set; }
}

public sealed class RevealFileRequest
{
    /// <summary>Absolute path of the file (or folder) to select in its parent folder.</summary>
    public string Path { get; set; } = "";

    public bool NewWindow { get; set; }
}

/// <summary>Result of finder.openFolder / finder.revealFile.</summary>
public sealed class FinderOpenResult
{
    /// <summary>"opened", "reused" (existing window) or "selected".</summary>
    public string State { get; set; } = "opened";
}

/// <summary>Why a file is being reported to Finder.</summary>
public static class ReceivedFileKinds
{
    public const string Download = "download";
    public const string Screenshot = "screenshot";
    public const string AddedToDownloads = "addedToDownloads";
}

public sealed class ReceivedFile
{
    public string Path { get; set; } = "";
    /// <summary>One of <see cref="ReceivedFileKinds"/>.</summary>
    public string Kind { get; set; } = ReceivedFileKinds.Download;
    public long? Size { get; set; }
}

public sealed class FilesReceivedRequest
{
    public List<ReceivedFile> Files { get; set; } = new();

    /// <summary>True when DownloadDock suggests highlighting the files (Finder may ignore).</summary>
    public bool Highlight { get; set; }
}

public sealed class FilesReceivedResult
{
    public int Accepted { get; set; }
}

// ------------------------------------------------------------------ events

public sealed class FileEventPayload
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long? Size { get; set; }
    /// <summary>Containing folder (for refresh decisions).</summary>
    public string Folder { get; set; } = "";
    /// <summary>For screenshots: "fullscreen", "region", "clipboard", "windows-file".</summary>
    public string? Source { get; set; }
}

public sealed class FolderChangedPayload
{
    public string Path { get; set; } = "";
}

public sealed class FanStatePayload
{
    public bool Open { get; set; }
}
