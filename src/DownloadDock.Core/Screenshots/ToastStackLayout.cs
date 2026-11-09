using DownloadDock.Core.Fan;

namespace DownloadDock.Core.Screenshots;

/// <summary>Positions floating screenshot thumbnails: bottom-right of the work area, newest at the bottom, older ones stacked above.</summary>
public static class ToastStackLayout
{
    public const double MaxWidth = 220;
    public const double MaxHeight = 160;
    public const double MinSide = 48;
    /// <summary>Distance from the work-area edges; larger than the window's shadow margin so nothing overlaps the taskbar.</summary>
    public const double Margin = 24;
    public const double Gap = 12;

    /// <summary>Fits an image into the thumbnail box, keeping its aspect ratio (panoramas/receipts stay usable).</summary>
    public static (double Width, double Height) FitThumbnail(double imageWidth, double imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return (MaxWidth, MaxHeight * 0.6);
        var scale = Math.Min(MaxWidth / imageWidth, MaxHeight / imageHeight);
        return (Math.Max(MinSide, imageWidth * scale), Math.Max(MinSide, imageHeight * scale));
    }

    /// <summary>
    /// Returns the top-left of each thumbnail (index 0 = newest, at the bottom). Thumbnails that would leave
    /// the top of the work area are reported as not fitting (the caller dismisses the oldest ones).
    /// </summary>
    public static IReadOnlyList<(double X, double Y, bool Fits)> Arrange(RectD workArea, IReadOnlyList<(double Width, double Height)> sizes)
    {
        var result = new List<(double, double, bool)>(sizes.Count);
        var bottom = workArea.Bottom - Margin;
        foreach (var (w, h) in sizes)
        {
            var y = bottom - h;
            var x = workArea.Right - Margin - w;
            result.Add((x, y, y >= workArea.Top + Margin));
            bottom = y - Gap;
        }
        return result;
    }
}
