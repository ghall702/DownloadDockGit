namespace DownloadDock.Core.Screenshots;

/// <summary>
/// Decides when a Windows screenshot (Snipping Tool, Win+Shift+S, Print Screen, Win+PrtScn) happened,
/// from two independent, supported signals:
///  • a new image file in the Screenshots folder (Snipping Tool auto-save, Win+PrtScn), and
///  • a clipboard image whose owner is a Windows capture tool, or that follows a Print Screen key press.
/// One capture often produces both signals (Snipping Tool copies AND saves), so a clipboard capture waits
/// briefly for its file; only if no file appears is the clipboard image used. Pure logic, clock injected.
/// </summary>
public sealed class ScreenshotCorrelator
{
    /// <summary>Process names that put screenshots on the clipboard (Windows 10/11 capture tools).</summary>
    public static readonly IReadOnlySet<string> CaptureToolProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool", "ScreenClippingHost", "ScreenSketch",
    };

    private readonly object _gate = new();
    private DateTime? _printScreenAt;
    private DateTime? _clipboardPendingSince;
    private DateTime? _clipboardShownAt;

    /// <summary>How long a clipboard capture waits for the matching auto-saved file.</summary>
    public TimeSpan FileWait { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>A clipboard image within this time after Print Screen counts as a screenshot.</summary>
    public TimeSpan PrintScreenWindow { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>A file arriving this soon after a clipboard capture was shown is the same capture.</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromSeconds(8);

    public void PrintScreenPressed(DateTime now)
    {
        lock (_gate) _printScreenAt = now;
    }

    /// <summary>Is a new clipboard image a screenshot? (Never true for ordinary images copied in other apps.)</summary>
    public bool IsScreenshotClipboard(string? ownerProcess, DateTime now)
    {
        if (ownerProcess is not null && CaptureToolProcesses.Contains(ownerProcess)) return true;
        lock (_gate)
            return _printScreenAt is { } t && now >= t && now - t <= PrintScreenWindow;
    }

    /// <summary>Records a clipboard screenshot; call <see cref="ResolvePendingClipboard"/> once <see cref="FileWait"/> has passed.</summary>
    public void ClipboardScreenshotArrived(DateTime now)
    {
        lock (_gate)
        {
            _clipboardPendingSince = now;
            _printScreenAt = null; // one key press, one capture
        }
    }

    /// <summary>A new screenshot file appeared. Returns true when it should be shown.</summary>
    public bool FileScreenshotArrived(DateTime now)
    {
        lock (_gate)
        {
            if (_clipboardPendingSince is not null)
            {
                // The pending clipboard capture's own file: show the file rather than a clipboard copy.
                _clipboardPendingSince = null;
                return true;
            }
            if (_clipboardShownAt is { } shown && now - shown <= DuplicateWindow)
            {
                // Already shown from the clipboard; this is the same capture's file arriving late.
                _clipboardShownAt = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Returns true exactly once when a pending clipboard capture should be shown from the clipboard
    /// (its wait expired with no file). False while still waiting or when nothing is pending.
    /// </summary>
    public bool ResolvePendingClipboard(DateTime now)
    {
        lock (_gate)
        {
            if (_clipboardPendingSince is not { } since || now - since < FileWait) return false;
            _clipboardPendingSince = null;
            _clipboardShownAt = now;
            return true;
        }
    }

    public bool HasPendingClipboard { get { lock (_gate) return _clipboardPendingSince is not null; } }
}
