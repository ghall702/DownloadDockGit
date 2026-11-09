using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadDock.Core.Previews;

namespace DownloadDock.Thumbnails;

/// <summary>
/// The per-format preview pipeline. Each format tries the highest-fidelity source first and
/// degrades gracefully; a file-type icon is only used when no content preview is possible.
///
///  Images  : WIC decode (EXIF-rotated) → Shell thumbnail
///  PDF     : PDFium first page → Shell thumbnail
///  Office  : Shell thumbnail (Office's own provider, if installed) → embedded docProps thumbnail
///            → built-in safe renderer (DOCX first page / PPTX first slide / XLSX first sheet)
///  ODF     : embedded Thumbnails/thumbnail.png → Shell
///  Text    : Shell → built-in text page renderer
///  Others  : Shell thumbnail (videos etc.) → Shell icon
/// </summary>
internal static class ThumbnailRenderer
{
    public static ThumbnailResult? Render(string path, int pixels)
    {
        var kind = FileKinds.Classify(path);
        try
        {
            var content = RenderContent(path, kind, pixels);
            if (content is not null) return content;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Preview failed for {path}: {ex.GetType().Name}: {ex.Message}");
        }

        var icon = Try(() => ShellThumbnailer.GetIcon(path, pixels));
        if (icon is not null) return new ThumbnailResult(icon, true, "shell-icon");
        return new ThumbnailResult(GenericIcon(Path.GetExtension(path), pixels), true, "generic");
    }

    private static ThumbnailResult? RenderContent(string path, PreviewKind kind, int px)
    {
        BitmapSource? img;
        switch (kind)
        {
            case PreviewKind.Image:
                if ((img = Try(() => ImageThumbnailer.Render(path, px))) is not null) return new(img, false, "wic");
                if ((img = Try(() => ShellThumbnailer.GetThumbnail(path, px))) is not null) return new(img, false, "shell");
                return null;

            case PreviewKind.Pdf:
                if ((img = Try(() => PdfThumbnailer.Render(path, px))) is not null) return new(img, false, "pdfium");
                if ((img = Try(() => ShellThumbnailer.GetThumbnail(path, px))) is not null) return new(img, false, "shell");
                return null;

            case PreviewKind.WordDocument:
            case PreviewKind.Presentation:
            case PreviewKind.Spreadsheet:
                if ((img = Try(() => ShellThumbnailer.GetThumbnail(path, px))) is not null) return new(img, false, "shell");
                if ((img = Try(() => DecodeEmbedded(path, px))) is not null) return new(img, false, "embedded");
                img = kind switch
                {
                    PreviewKind.WordDocument => Try(() => DocxParser.Parse(path) is { } d ? DocumentPreviewRenderer.RenderDocument(d, px) : null),
                    PreviewKind.Presentation => Try(() => PptxParser.Parse(path) is { } s ? DocumentPreviewRenderer.RenderSlide(s, px) : null),
                    _ => Try(() => XlsxParser.Parse(path) is { } x ? DocumentPreviewRenderer.RenderSheet(x, px) : null),
                };
                return img is null ? null : new(img, false, "rendered");

            case PreviewKind.OpenDocument:
                if ((img = Try(() => DecodeEmbedded(path, px))) is not null) return new(img, false, "embedded");
                if ((img = Try(() => ShellThumbnailer.GetThumbnail(path, px))) is not null) return new(img, false, "shell");
                return null;

            case PreviewKind.Text:
                if ((img = Try(() => ShellThumbnailer.GetThumbnail(path, px))) is not null) return new(img, false, "shell");
                var lines = TextPreviewReader.TryRead(path);
                if (lines is null) return null;
                img = DocumentPreviewRenderer.RenderText(lines, Path.GetExtension(path), px);
                return new(img, false, "text");

            default:
                if ((img = Try(() => ShellThumbnailer.GetThumbnail(path, px))) is not null) return new(img, false, "shell");
                return null;
        }
    }

    private static BitmapSource? DecodeEmbedded(string path, int px)
    {
        var bytes = EmbeddedThumbnail.TryExtract(path);
        if (bytes is null) return null;
        using var ms = new MemoryStream(bytes, writable: false);
        return ImageThumbnailer.Render(ms, px);
    }

    private static BitmapSource? Try(Func<BitmapSource?> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Preview stage failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Last-resort drawn icon: a page with the extension label.</summary>
    internal static BitmapSource GenericIcon(string extension, int px)
    {
        var label = extension.TrimStart('.').ToUpperInvariant();
        if (label.Length > 5) label = label[..5];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var w = px * 0.72;
            var h = px * 0.92;
            var x = (px - w) / 2;
            var y = (px - h) / 2;
            var fold = w * 0.28;
            var page = new StreamGeometry();
            using (var g = page.Open())
            {
                g.BeginFigure(new Point(x, y), true, true);
                g.LineTo(new Point(x + w - fold, y), true, false);
                g.LineTo(new Point(x + w, y + fold), true, false);
                g.LineTo(new Point(x + w, y + h), true, false);
                g.LineTo(new Point(x, y + h), true, false);
            }
            page.Freeze();
            dc.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)), Math.Max(1, px / 96.0)), page);
            if (label.Length > 0)
            {
                var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    px * 0.16, new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0xB8)), 1.0);
                dc.DrawText(ft, new Point((px - ft.Width) / 2, y + h * 0.58));
            }
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
