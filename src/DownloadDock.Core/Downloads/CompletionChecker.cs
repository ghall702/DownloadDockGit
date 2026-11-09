namespace DownloadDock.Core.Downloads;

/// <summary>Abstraction over file probing so completion logic can be unit tested.</summary>
public interface IFileProbe
{
    /// <summary>True when another process has the file open for writing (or it can't be read).</summary>
    bool IsLockedForWriting(string path);

    bool FileExists(string path);
}

public sealed class RealFileProbe : IFileProbe
{
    public static readonly RealFileProbe Instance = new();

    public bool IsLockedForWriting(string path)
    {
        try
        {
            // Denying FileShare.Write fails with a sharing violation if a writer currently has it open.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
            return false;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // readable-protected but finished; let the shell decide
        catch (IOException) { return true; }
    }

    public bool FileExists(string path) => File.Exists(path);
}

/// <summary>
/// Decides whether a listed file is a completed download or is still being written.
/// </summary>
public sealed class CompletionChecker
{
    private static readonly string[] SiblingPartialSuffixes = { ".part", ".crdownload", ".partial", ".download", ".opdownload" };

    private readonly IFileProbe _probe;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Files modified more recently than this are re-checked before being shown.</summary>
    public TimeSpan SettlePeriod { get; init; } = TimeSpan.FromSeconds(1.5);

    public CompletionChecker(IFileProbe? probe = null, Func<DateTime>? utcNow = null)
    {
        _probe = probe ?? RealFileProbe.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public enum State { Complete, Pending }

    public State Check(DownloadEntry entry)
    {
        // Firefox creates an empty placeholder "file.pdf" next to "file.pdf.part" while downloading.
        foreach (var suffix in SiblingPartialSuffixes)
        {
            if (_probe.FileExists(entry.FullPath + suffix))
                return State.Pending;
        }

        var age = _utcNow() - entry.LastWriteTimeUtc;
        if (age < SettlePeriod)
        {
            // Still being written moments ago – only accept it if nobody holds it open for writing.
            return _probe.IsLockedForWriting(entry.FullPath) ? State.Pending : (entry.Length == 0 ? State.Pending : State.Complete);
        }

        // Older files: cheap lock check only for very recent ones to keep scans of huge folders fast.
        if (age < TimeSpan.FromMinutes(2) && _probe.IsLockedForWriting(entry.FullPath))
            return State.Pending;

        return State.Complete;
    }
}
