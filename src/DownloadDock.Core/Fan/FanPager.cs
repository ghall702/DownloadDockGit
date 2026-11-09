namespace DownloadDock.Core.Fan;

/// <summary>
/// Overflow handling that preserves the fan: when more downloads exist than fit on screen,
/// the fan shows a window of <see cref="Capacity"/> files and the mouse wheel (or arrow keys)
/// slides that window towards older files.
/// </summary>
public sealed class FanPager
{
    public int TotalCount { get; private set; }
    public int Capacity { get; private set; }
    public int Offset { get; private set; }

    public void Update(int totalCount, int capacity)
    {
        TotalCount = Math.Max(0, totalCount);
        Capacity = Math.Max(0, capacity);
        Offset = Clamp(Offset);
    }

    public void Reset() => Offset = 0;

    public bool CanScrollOlder => Offset + Capacity < TotalCount;
    public bool CanScrollNewer => Offset > 0;

    /// <summary>Number of files hidden above the fan (older).</summary>
    public int HiddenOlder => Math.Max(0, TotalCount - (Offset + Capacity));

    /// <summary>Number of files hidden below the fan (newer).</summary>
    public int HiddenNewer => Offset;

    /// <summary>Scrolls by <paramref name="delta"/> files (positive = older). Returns true if the window moved.</summary>
    public bool Scroll(int delta)
    {
        var next = Clamp(Offset + delta);
        if (next == Offset) return false;
        Offset = next;
        return true;
    }

    public (int Start, int Count) VisibleRange => (Offset, Math.Max(0, Math.Min(Capacity, TotalCount - Offset)));

    private int Clamp(int offset) => Math.Max(0, Math.Min(offset, Math.Max(0, TotalCount - Capacity)));
}
