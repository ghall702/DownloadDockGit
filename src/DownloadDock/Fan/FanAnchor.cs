using DownloadDock.Core.Fan;
using DownloadDock.Interop;

namespace DownloadDock.Fan;

/// <summary>Where the fan starts, in physical screen pixels, plus the target monitor's geometry.</summary>
internal sealed record FanAnchor(
    RECT Monitor,
    RECT WorkArea,
    double DpiScale,
    double AnchorXPx,
    double AnchorEdgeYPx,
    FanDirection Direction,
    bool IsExact)
{
    /// <summary>Converts to the DIP coordinate space used by the layout calculator.</summary>
    public RectD WorkAreaDip => new(WorkArea.Left / DpiScale, WorkArea.Top / DpiScale, WorkArea.Width / DpiScale, WorkArea.Height / DpiScale);
    public double AnchorXDip => AnchorXPx / DpiScale;
    public double AnchorEdgeYDip => AnchorEdgeYPx / DpiScale;
}

/// <summary>Resolves the fan anchor from the tray icon, falling back to the click point / notification area.</summary>
internal static class FanAnchorResolver
{
    public static FanAnchor Resolve(RECT? iconRect, POINT? clickPoint)
    {
        POINT probe;
        var exact = false;
        if (iconRect is { } r)
        {
            probe = new POINT(r.Left + r.Width / 2, r.Top + r.Height / 2);
            exact = true;
        }
        else if (clickPoint is { } c && (c.X != 0 || c.Y != 0))
        {
            probe = c;
        }
        else
        {
            NativeMethods.GetCursorPos(out probe);
        }

        var hMon = NativeMethods.MonitorFromPoint(probe, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(hMon, ref mi);
        var scale = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                scale = dx / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        var mon = mi.rcMonitor;
        var work = mi.rcWork;
        var gap = (int)Math.Round(4 * scale);

        // Fallback when Windows doesn't expose the icon position: the right end of the
        // notification area, i.e. just left of the clock.
        if (!exact && iconRect is null && clickPoint is null)
            probe = new POINT(work.Right - (int)(150 * scale), work.Bottom);

        FanDirection direction;
        double edgeY;
        if (work.Bottom < mon.Bottom)
        {
            direction = FanDirection.Up;
            edgeY = exact ? Math.Min(work.Bottom, iconRect!.Value.Top - gap) : work.Bottom;
        }
        else if (work.Top > mon.Top)
        {
            direction = FanDirection.Down;
            edgeY = exact ? Math.Max(work.Top, iconRect!.Value.Bottom + gap) : work.Top;
        }
        else
        {
            // Auto-hide taskbar or vertical taskbar: decide by which half the icon is in.
            var top = exact ? iconRect!.Value.Top : probe.Y - (int)(24 * scale);
            var bottom = exact ? iconRect!.Value.Bottom : probe.Y + (int)(24 * scale);
            if (probe.Y >= (mon.Top + mon.Bottom) / 2)
            {
                direction = FanDirection.Up;
                edgeY = Math.Min(top - gap, work.Bottom);
            }
            else
            {
                direction = FanDirection.Down;
                edgeY = Math.Max(bottom + gap, work.Top);
            }
        }

        var anchorX = Math.Clamp(probe.X, work.Left, work.Right);
        return new FanAnchor(mon, work, scale, anchorX, edgeY, direction, exact);
    }
}
