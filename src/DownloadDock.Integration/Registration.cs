using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DownloadDock.Integration;

/// <summary>An application's self-registration (written by the app itself, read by its peer).</summary>
public sealed record AppRegistration(string ExecutablePath, string? Version, string? ProtocolVersion, bool AllowLaunchByPeer);

/// <summary>
/// Install-identity registration in the registry, so each app can find (and, if allowed, start) the other
/// WITHOUT ever taking an executable path from an IPC message:
/// <code>
/// HKCU\Software\FinderForWindows\Integration   (or HKLM for per-machine installs)
/// HKCU\Software\DownloadDock\Integration
///   ExecutablePath    REG_SZ    absolute path of the .exe
///   Version           REG_SZ    application version
///   ProtocolVersion   REG_SZ    integration protocol "major.minor"
///   AllowLaunchByPeer REG_DWORD 1 = the other app may start this one; 0 = never
/// </code>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppRegistrationStore
{
    public const string FinderKey = @"Software\FinderForWindows\Integration";
    public const string DownloadDockKey = @"Software\DownloadDock\Integration";

    public static AppRegistration? ReadFinder() => Read(FinderKey);

    public static AppRegistration? ReadDownloadDock() => Read(DownloadDockKey);

    /// <summary>Reads HKCU first, then HKLM. Returns null unless the registered executable exists.</summary>
    public static AppRegistration? Read(string subKey)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key?.GetValue("ExecutablePath") is not string exe) continue;
                if (!IsAcceptableExecutable(exe)) continue;
                var allow = key.GetValue("AllowLaunchByPeer") is not int a || a != 0;
                return new AppRegistration(exe, key.GetValue("Version") as string, key.GetValue("ProtocolVersion") as string, allow);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return null;
    }

    /// <summary>Writes (or updates) this application's registration under HKCU.</summary>
    public static void Write(string subKey, AppRegistration registration)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue("ExecutablePath", registration.ExecutablePath, RegistryValueKind.String);
        if (registration.Version is not null) key.SetValue("Version", registration.Version, RegistryValueKind.String);
        key.SetValue("ProtocolVersion", registration.ProtocolVersion ?? IntegrationProtocol.Version, RegistryValueKind.String);
        key.SetValue("AllowLaunchByPeer", registration.AllowLaunchByPeer ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>A registered path is only used if it is an absolute, valid path to an existing .exe file.</summary>
    public static bool IsAcceptableExecutable(string path)
        => PathRules.IsValidAbsolutePath(path)
           && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && !path.StartsWith(@"\\", StringComparison.Ordinal) // never launch from a network share
           && File.Exists(path);
}
