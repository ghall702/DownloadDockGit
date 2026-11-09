using System.Globalization;

namespace DownloadDock.Integration;

/// <summary>
/// Compatibility policy: peers are compatible when their MAJOR versions are equal. The session uses the
/// lower MINOR of the two, and each side only uses features from the negotiated minor and the
/// capabilities the other side advertised.
/// </summary>
public static class VersionNegotiation
{
    public static bool TryParse(string? version, out int major, out int minor)
    {
        major = minor = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version.Split('.');
        if (parts.Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) || major < 0) return false;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor) || minor < 0)) return false;
        return true;
    }

    /// <summary>Returns the negotiated "major.minor", or null when incompatible.</summary>
    public static string? Negotiate(string? peerVersion, int ourMajor = IntegrationProtocol.MajorVersion, int ourMinor = IntegrationProtocol.MinorVersion)
    {
        if (!TryParse(peerVersion, out var major, out var minor)) return null;
        if (major != ourMajor) return null;
        return $"{major}.{Math.Min(minor, ourMinor)}";
    }
}
