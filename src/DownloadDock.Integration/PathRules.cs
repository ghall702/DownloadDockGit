namespace DownloadDock.Integration;

/// <summary>
/// Validates Windows paths received over IPC (pure string rules – identical on every OS, no I/O).
/// Accepted: absolute drive paths (C:\…), UNC (\\server\share\…) and their extended-length forms
/// (\\?\C:\…, \\?\UNC\server\share\…). Mapped drives are ordinary drive paths.
/// Rejected: relative or drive-relative paths, device namespaces (\\.\…, \\?\GLOBALROOT…), "." / ".."
/// segments, wildcards and other invalid characters, alternate data streams and over-long paths.
/// </summary>
public static class PathRules
{
    private static readonly char[] InvalidChars = { '<', '>', '"', '|', '?', '*' };

    public static bool IsValidAbsolutePath(string? path) => Validate(path) is null;

    /// <summary>Returns null when valid, otherwise a human-readable reason.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Path is empty.";
        if (path.Length > IntegrationProtocol.MaxPathLength) return "Path is too long.";
        foreach (var c in path)
        {
            if (c < 0x20) return "Path contains control characters.";
        }
        if (path != path.Trim()) return "Path has leading or trailing whitespace.";
        path = path.Replace('/', '\\');

        string rest;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var inner = path[4..];
            if (inner.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                return ValidateUncBody(inner[4..]);
            if (inner.StartsWith("GLOBALROOT", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
                return "Device and volume GUID paths are not accepted.";
            return ValidateDriveBody(inner);
        }
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return "Device paths are not accepted.";
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return ValidateUncBody(path[2..]);
        rest = path;
        return ValidateDriveBody(rest);
    }

    private static string? ValidateDriveBody(string p)
    {
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
            return "Path must be absolute (for example C:\\Folder or \\\\server\\share\\Folder).";
        return ValidateSegments(p[3..], allowEmpty: true);
    }

    private static string? ValidateUncBody(string p)
    {
        var parts = p.Split('\\');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return "UNC paths need a server and a share (\\\\server\\share\\…).";
        if (parts[0] is "." or "?") return "Device paths are not accepted.";
        foreach (var name in new[] { parts[0], parts[1] })
        {
            if (name.IndexOfAny(InvalidChars) >= 0 || name.Contains(':')) return "Invalid server or share name.";
        }
        return ValidateSegments(string.Join('\\', parts.Skip(2)), allowEmpty: true);
    }

    private static string? ValidateSegments(string p, bool allowEmpty)
    {
        if (p.Length == 0) return allowEmpty ? null : "Path is incomplete.";
        var segments = p.Split('\\');
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s.Length == 0)
            {
                if (i == segments.Length - 1) continue; // trailing backslash on a folder
                return "Path contains an empty segment (\\\\).";
            }
            if (s is "." or "..") return "Path must not contain '.' or '..' segments.";
            if (s.IndexOfAny(InvalidChars) >= 0) return "Path contains invalid characters (< > \" | ? *).";
            if (s.Contains(':')) return "Alternate data streams are not accepted.";
            if (s.EndsWith(' ') || (s.EndsWith('.') && s.Trim('.').Length > 0)) return "Path segments must not end with a space or dot.";
        }
        return null;
    }

    /// <summary>Containing folder of a validated path (string-only, works for drive and UNC paths).</summary>
    public static string? ParentOf(string path)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\');
        var i = p.LastIndexOf('\\');
        if (i <= 0) return null;
        var parent = p[..i];
        if (parent.Length == 2 && parent[1] == ':') parent += "\\";
        return parent;
    }
}
