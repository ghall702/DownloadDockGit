namespace DownloadDock.Core.Downloads;

/// <summary>Helpers for files dropped onto the fan (copied into the Downloads folder).</summary>
public static class DropIntoFolder
{
    /// <summary>"photo.png" → "photo (2).png", "photo (3).png", … – never overwrites.</summary>
    public static string UniqueCopyPath(string folder, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var candidate = Path.Combine(folder, fileName);
        if (!exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>True when every path already lives directly in <paramref name="folder"/> (dragging a fan item back onto the fan).</summary>
    public static bool AllAlreadyIn(IEnumerable<string> paths, string folder)
    {
        var target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return paths.All(p => string.Equals(Path.GetDirectoryName(Path.GetFullPath(p))?.TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// "Spring-loading": fires once when the pointer has rested inside a target long enough during a drag
/// (macOS opens a Dock stack when you hover a dragged file over it). Pure logic, clock supplied by caller.
/// </summary>
public sealed class DwellTrigger
{
    private DateTime? _enteredAt;
    private bool _fired;

    public TimeSpan Dwell { get; init; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Feed the current state; returns true exactly once per stay inside the target.</summary>
    public bool Update(bool inside, DateTime now)
    {
        if (!inside)
        {
            _enteredAt = null;
            _fired = false;
            return false;
        }
        _enteredAt ??= now;
        if (_fired || now - _enteredAt.Value < Dwell) return false;
        _fired = true;
        return true;
    }

    public void Reset()
    {
        _enteredAt = null;
        _fired = false;
    }
}
