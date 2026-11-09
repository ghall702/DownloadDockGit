using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DownloadDock.Tray;

/// <summary>
/// Draws the notification-area glyph (a Fluent-style "download into tray" arrow) at the exact
/// pixel size Windows asks for, in white for a dark taskbar or near-black for a light one.
/// </summary>
internal static class TrayIconRenderer
{
    public static BitmapSource Render(int sizePx, bool darkTaskbar)
    {
        var color = darkTaskbar ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var unit = sizePx / 16.0;
        var pen = new Pen(brush, Math.Max(1.0, 1.35 * unit))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Point P(double x, double y) => new(x * unit, y * unit);
            // Arrow shaft and head.
            dc.DrawLine(pen, P(8, 1.8), P(8, 10.2));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(P(4.4, 6.9), false, false);
                g.LineTo(P(8, 10.5), true, true);
                g.LineTo(P(11.6, 6.9), true, true);
            }
            head.Freeze();
            dc.DrawGeometry(null, pen, head);
            // Tray.
            var tray = new StreamGeometry();
            using (var g = tray.Open())
            {
                g.BeginFigure(P(2.2, 10.6), false, false);
                g.LineTo(P(2.2, 13.8), true, true);
                g.LineTo(P(13.8, 13.8), true, true);
                g.LineTo(P(13.8, 10.6), true, true);
            }
            tray.Freeze();
            dc.DrawGeometry(null, pen, tray);
        }

        var rtb = new RenderTargetBitmap(sizePx, sizePx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
