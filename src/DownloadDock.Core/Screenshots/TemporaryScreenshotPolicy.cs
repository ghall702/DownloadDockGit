namespace DownloadDock.Core.Screenshots;

/// <summary>
/// Which temporary screenshots may be deleted. A file is only removed when it is old enough that any
/// application it was dropped into has long finished reading it, and never while it is being dragged.
/// </summary>
public static class TemporaryScreenshotPolicy
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);

    public static IReadOnlyList<string> FilesToDelete(
        IEnumerable<(string Path, DateTime LastWriteUtc)> files,
        DateTime nowUtc,
        IReadOnlySet<string> inUse,
        TimeSpan? maxAge = null)
    {
        var age = maxAge ?? DefaultMaxAge;
        return files
            .Where(f => nowUtc - f.LastWriteUtc > age && !inUse.Contains(f.Path))
            .Select(f => f.Path)
            .ToList();
    }
}
