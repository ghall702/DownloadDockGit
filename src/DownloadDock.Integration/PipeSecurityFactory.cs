using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DownloadDock.Integration;

/// <summary>
/// Access control for the server pipes (Windows): only the current logon session (or, if the logon SID
/// can't be determined, the current user) may connect; network access is explicitly denied.
/// Note: this restricts *who* (which user/session) can connect – it does not authenticate *which
/// executable* is connecting. See "Security considerations" in docs/FINDER_INTEGRATION.md.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    /// <summary>NT AUTHORITY\NETWORK – present in the token of any remote (SMB) client.</summary>
    public static readonly SecurityIdentifier NetworkSid = new(WellKnownSidType.NetworkSid, null);

    public static PipeSecurity Create()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = LogonSid(identity) ?? identity.User!;
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(NetworkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(principal, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (identity.User is { } user)
            security.SetOwner(user);
        return security;
    }

    /// <summary>The logon-session SID (S-1-5-5-x-y) of the current token, if present.</summary>
    public static SecurityIdentifier? LogonSid(WindowsIdentity identity)
        => identity.Groups?.OfType<SecurityIdentifier>().FirstOrDefault(s => s.Value.StartsWith("S-1-5-5-", StringComparison.Ordinal));
}
