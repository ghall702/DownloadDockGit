namespace DownloadDock.Core.Downloads;

/// <summary>An immutable snapshot of one completed download.</summary>
public sealed record DownloadEntry(
    string FullPath,
    string Name,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc)
{
    /// <summary>
    /// Best approximation of "date added" (the macOS Downloads stack sort key).
    /// A freshly downloaded file has a new creation time; a file copied/extracted into
    /// the folder may keep an old write time but gets a new creation time, and a file
    /// that is re-saved gets a new write time. The later of the two wins.
    /// </summary>
    public DateTime AddedUtc => CreationTimeUtc > LastWriteTimeUtc ? CreationTimeUtc : LastWriteTimeUtc;

    public string Extension => Path.GetExtension(Name);

    /// <summary>Identity used for change detection and thumbnail cache keys.</summary>
    public string VersionKey => $"{FullPath}|{Length}|{LastWriteTimeUtc.Ticks}";
}
