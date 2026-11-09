using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DownloadDock.Fan;

/// <summary>A drawn Windows 11-style folder used until the Shell's own folder icon has loaded.</summary>
internal static class FolderGlyph
{
    private static BitmapSource? _cached;

    public static BitmapSource Get()
    {
        if (_cached is not null) return _cached;
        const int size = 256;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var back = new SolidColorBrush(Color.FromRgb(0xE8, 0xA8, 0x2E));
            var front = new LinearGradientBrush(Color.FromRgb(0xFF, 0xD8, 0x6A), Color.FromRgb(0xF6, 0xBE, 0x3E), 90);
            // Back panel with tab.
            var tab = new StreamGeometry();
            using (var g = tab.Open())
            {
                g.BeginFigure(new Point(24, 60), true, true);
                g.LineTo(new Point(100, 60), true, true);
                g.LineTo(new Point(124, 84), true, true);
                g.LineTo(new Point(232, 84), true, true);
                g.LineTo(new Point(232, 200), true, true);
                g.LineTo(new Point(24, 200), true, true);
            }
            tab.Freeze();
            dc.DrawGeometry(back, null, tab);
            // Front panel.
            dc.DrawRoundedRectangle(front, null, new Rect(24, 100, 208, 116), 14, 14);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        _cached = rtb;
        return rtb;
    }
}
