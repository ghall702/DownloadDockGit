namespace DownloadDock.Core.Fan;

/// <summary>Which way the fan grows from the anchor (depends on taskbar edge).</summary>
public enum FanDirection
{
    /// <summary>Taskbar at the bottom: fan rises upwards (the normal Windows 11 case).</summary>
    Up,
    /// <summary>Taskbar at the top: fan hangs downwards.</summary>
    Down,
}

/// <summary>Simple rectangle in device-independent units (no WPF dependency).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public RectD Union(RectD o)
    {
        var l = Math.Min(Left, o.Left);
        var t = Math.Min(Top, o.Top);
        var r = Math.Max(Right, o.Right);
        var b = Math.Max(Bottom, o.Bottom);
        return new RectD(l, t, r - l, b - t);
    }

    public bool Contains(RectD o, double tolerance = 0.5)
        => o.Left >= Left - tolerance && o.Top >= Top - tolerance && o.Right <= Right + tolerance && o.Bottom <= Bottom + tolerance;
}

public sealed record FanLayoutOptions
{
    /// <summary>Edge length of the square box each thumbnail is fitted into (DIPs).</summary>
    public double ThumbnailSize { get; init; } = 96;

    /// <summary>Distance along the arc between consecutive thumbnail centres, as a fraction of <see cref="ThumbnailSize"/>. &lt; 1 gives overlap.</summary>
    public double SpacingFactor { get; init; } = 0.80;

    /// <summary>Total sweep of the arc at the top item, in degrees (macOS feel: ~20–25°).</summary>
    public double MaxSweepDegrees { get; init; } = 22;

    /// <summary>Smallest sweep we accept before mirroring the curve to the other side.</summary>
    public double MinSweepDegrees { get; init; } = 7;

    /// <summary>Width reserved to the left of each thumbnail for its name label.</summary>
    public double LabelWidth { get; init; } = 230;

    /// <summary>Gap between label and thumbnail.</summary>
    public double LabelGap { get; init; } = 10;

    /// <summary>Gap between the anchor (taskbar edge) and the first thumbnail.</summary>
    public double AnchorGap { get; init; } = 14;

    /// <summary>Margin kept from the work-area edges.</summary>
    public double ScreenMargin { get; init; } = 8;

    /// <summary>Extra padding around the computed bounds so shadows / hover scale aren't clipped.</summary>
    public double Padding { get; init; } = 28;

    /// <summary>Hover scale; used to reserve room.</summary>
    public double HoverScale { get; init; } = 1.08;
}

/// <summary>Placement of one item, in coordinates relative to the work area origin.</summary>
/// <param name="Index">0 = bottom-most item (closest to the anchor).</param>
/// <param name="CenterX">Centre of the thumbnail box.</param>
/// <param name="CenterY">Centre of the thumbnail box.</param>
/// <param name="AngleDegrees">Clockwise rotation applied around the thumbnail centre.</param>
/// <param name="ZIndex">Higher = drawn on top. Items closer to the anchor sit on top, like macOS.</param>
public readonly record struct FanItemPlacement(int Index, double CenterX, double CenterY, double AngleDegrees, int ZIndex);

public sealed record FanLayoutResult(
    IReadOnlyList<FanItemPlacement> Items,
    RectD Bounds,
    int RequestedCount,
    int FittedCount,
    bool Mirrored,
    double SweepDegrees,
    double Radius,
    FanDirection Direction);

/// <summary>
/// Computes the curved "Downloads stack" fan: thumbnails rise from the tray icon along a
/// large-radius circular arc that bends away from the anchor, each rotated to follow the
/// arc's tangent, overlapping their neighbours. Pure math – fully unit testable.
/// </summary>
public static class FanLayoutCalculator
{
    /// <summary>
    /// Lays out <paramref name="count"/> items.
    /// </summary>
    /// <param name="count">Requested number of items (including any "open folder" item).</param>
    /// <param name="anchorX">Horizontal centre of the tray icon (DIPs, same space as <paramref name="workArea"/>).</param>
    /// <param name="anchorEdgeY">Y of the work-area edge the fan grows from (top of the taskbar for <see cref="FanDirection.Up"/>).</param>
    /// <param name="workArea">Usable monitor area (excludes taskbar).</param>
    public static FanLayoutResult Calculate(int count, double anchorX, double anchorEdgeY, RectD workArea, FanDirection direction, FanLayoutOptions? options = null)
    {
        var o = options ?? new FanLayoutOptions();
        count = Math.Max(0, count);
        var s = o.ThumbnailSize;
        var spacing = Math.Max(4, s * o.SpacingFactor);
        var half = s / 2 * o.HoverScale;

        // The first card is centred on the icon; keep it on screen even for an icon at the very edge.
        anchorX = Math.Clamp(anchorX, workArea.Left + o.ScreenMargin + half, Math.Max(workArea.Left + o.ScreenMargin + half, workArea.Right - o.ScreenMargin - half));

        // ---- 1. Upper bound on how many items fit vertically ------------------------------
        var availableHeight = direction == FanDirection.Up
            ? anchorEdgeY - workArea.Top - o.ScreenMargin
            : workArea.Bottom - anchorEdgeY - o.ScreenMargin;
        availableHeight -= o.AnchorGap + 2 * half;

        var fitted = count;
        // Arc height is R sin(θ) with R = L/θ, which approaches L as the sweep shrinks; use L as
        // a first estimate, then verify with the exact rotated bounds below.
        while (fitted > 1 && (fitted - 1) * spacing > availableHeight)
            fitted--;
        if (availableHeight < 0) fitted = Math.Min(fitted, 1);

        // ---- 2. Place, then shrink until every rotated item (incl. its label) is on screen --
        while (true)
        {
            var result = Place(fitted, count, anchorX, anchorEdgeY, workArea, direction, o, spacing, half);
            if (fitted <= 1 || FitsVertically(result, workArea, o))
                return result;
            fitted--;
        }
    }

    private static bool FitsVertically(FanLayoutResult r, RectD work, FanLayoutOptions o)
    {
        foreach (var item in r.Items)
        {
            var b = ItemBounds(item, o);
            if (r.Direction == FanDirection.Up && b.Top < work.Top + o.ScreenMargin - 0.5) return false;
            if (r.Direction == FanDirection.Down && b.Bottom > work.Bottom - o.ScreenMargin + 0.5) return false;
        }
        return true;
    }

    private static FanLayoutResult Place(int fitted, int requested, double anchorX, double anchorEdgeY, RectD workArea, FanDirection direction,
        FanLayoutOptions o, double spacing, double half)
    {
        var s = o.ThumbnailSize;

        // ---- Choose sweep / curvature so the fan stays on screen horizontally -------------
        var arcLength = Math.Max(0, (fitted - 1) * spacing);
        var mirrored = false;
        var sweep = Deg2Rad(o.MaxSweepDegrees);
        var rightRoom = workArea.Right - o.ScreenMargin - anchorX - half;
        var leftRoom = anchorX - workArea.Left - o.ScreenMargin - half - o.LabelGap - o.LabelWidth;

        if (arcLength > 0)
        {
            // Horizontal drift of the top item = R(1 - cos θ) with R = L/θ.
            double Drift(double theta) => arcLength / theta * (1 - Math.Cos(theta));
            // Rotated corners stick out a little further than the centre.
            double Extra(double theta) => half * (Math.Abs(Math.Sin(theta)) + Math.Abs(Math.Cos(theta)) - 1);

            if (Drift(sweep) + Extra(sweep) > rightRoom)
            {
                // Reduce the sweep until it fits; binary search (drift is monotonic in θ).
                double lo = Deg2Rad(0.01), hi = sweep;
                for (var i = 0; i < 40; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (Drift(mid) + Extra(mid) > rightRoom) hi = mid; else lo = mid;
                }
                sweep = lo;
                if (Rad2Deg(sweep) < o.MinSweepDegrees && leftRoom > rightRoom)
                {
                    // Not enough room on the right (icon close to the screen edge): bend left instead.
                    mirrored = true;
                    sweep = Deg2Rad(o.MaxSweepDegrees);
                }
            }
        }

        // ---- Place items along the arc -----------------------------------------------------
        var radius = arcLength > 0 ? arcLength / sweep : double.PositiveInfinity;
        var items = new List<FanItemPlacement>(fitted);
        var vSign = direction == FanDirection.Up ? -1 : 1;
        var hSign = mirrored ? -1 : 1;
        var baseY = anchorEdgeY + vSign * (o.AnchorGap + s / 2);

        for (var i = 0; i < fitted; i++)
        {
            var dist = i * spacing;
            double dx, dy, angle;
            if (double.IsInfinity(radius) || dist == 0)
            {
                dx = 0; dy = dist; angle = 0;
            }
            else
            {
                var a = dist / radius;
                dx = radius * (1 - Math.Cos(a));
                dy = radius * Math.Sin(a);
                angle = Rad2Deg(a);
            }

            // Upward fan bending right => clockwise (positive) rotation, like the macOS stack.
            var rot = angle * hSign * (direction == FanDirection.Up ? 1 : -1);
            items.Add(new FanItemPlacement(
                i,
                anchorX + hSign * dx,
                baseY + vSign * dy,
                rot,
                ZIndex: fitted - i));
        }

        // ---- Bounds (thumbnail + label, rotated) -------------------------------------------
        var bounds = new RectD(anchorX, anchorEdgeY, 0, 0);
        foreach (var it in items)
            bounds = bounds.Union(ItemBounds(it, o));
        bounds = new RectD(bounds.X - o.Padding, bounds.Y - o.Padding, bounds.Width + 2 * o.Padding, bounds.Height + 2 * o.Padding);

        return new FanLayoutResult(items, bounds, requested, fitted, mirrored, Rad2Deg(sweep), radius, direction);
    }

    /// <summary>Axis-aligned bounds of an item (thumbnail box plus label to its left) after rotation and hover scale.</summary>
    public static RectD ItemBounds(FanItemPlacement p, FanLayoutOptions o)
    {
        var s = o.ThumbnailSize * o.HoverScale;
        var left = -(s / 2 + o.LabelGap + o.LabelWidth);
        var right = s / 2;
        var top = -s / 2;
        var bottom = s / 2;
        var rad = Deg2Rad(p.AngleDegrees);
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
        {
            // Clockwise rotation in screen space (y down).
            var rx = x * cos - y * sin;
            var ry = x * sin + y * cos;
            minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
        }
        return new RectD(p.CenterX + minX, p.CenterY + minY, maxX - minX, maxY - minY);
    }

    public static double Deg2Rad(double d) => d * Math.PI / 180.0;
    public static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
