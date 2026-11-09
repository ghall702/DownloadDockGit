using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DownloadDock.Integration;

/// <summary>
/// Named-pipe endpoint names. Each application hosts its own server pipe (no connection conflicts),
/// scoped to the current user AND Windows logon session, and versioned by protocol major:
/// <code>DownloadDock.Integration.v1.&lt;key&gt;</code> and <code>FinderForWindows.Integration.v1.&lt;key&gt;</code>
/// where key = first 16 hex chars of SHA-256("&lt;user SID&gt;|&lt;session id&gt;").
/// Both applications compute the same key independently.
/// </summary>
public static class IntegrationEndpoints
{
    public const string DownloadDockPrefix = "DownloadDock.Integration";
    public const string FinderPrefix = "FinderForWindows.Integration";

    public static string DownloadDockPipeName() => Name(DownloadDockPrefix);

    public static string FinderPipeName() => Name(FinderPrefix);

    public static string Name(string prefix, int major = IntegrationProtocol.MajorVersion)
        => $"{prefix}.v{major}.{SessionKey()}";

    /// <summary>Per user + logon session key (stable for the session, different for every other user/session).</summary>
    public static string SessionKey()
    {
        string user;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.User?.Value ?? Environment.UserName;
        }
        else
        {
            user = Environment.UserName; // tests on Linux/macOS (Unix domain sockets)
        }
        var session = Process.GetCurrentProcess().SessionId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{user}|{session}"));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
