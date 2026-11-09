using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadDock.Core.Tests;

namespace DownloadDock.Windows.Tests;

/// <summary>Creates realistic sample downloads (PDF, images, Office files, archives, text).</summary>
internal static class SampleFiles
{
    /// <summary>Writes a one-page PDF with a red block and a title (hand-built, valid xref).</summary>
    public static string Pdf(string folder, string name = "Project Brief.pdf", double width = 612, double height = 792)
    {
        var content = """
            1 1 1 rg 0 0 612 792 re f
            0.85 0.1 0.1 rg 156 246 300 300 re f
            0.1 0.2 0.5 rg 72 680 468 4 re f
            BT /F1 36 Tf 0 0 0 rg 72 700 Td (Project Brief) Tj ET
            BT /F1 12 Tf 72 640 Td (Prepared for the DownloadDock preview test.) Tj ET
            """;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }

    /// <summary>Draws a "beach" landscape and saves it as PNG or JPEG (optionally with an EXIF orientation).</summary>
    public static string Image(string folder, string name, int w = 800, int h = 500, ushort? orientation = null)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x4F, 0xA9, 0xF0), Color.FromRgb(0xBF, 0xE6, 0xFF), 90), null, new Rect(0, 0, w, h * 0.55));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x9C, 0xC4)), null, new Rect(0, h * 0.55, w, h * 0.15));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xDC, 0xA8)), null, new Rect(0, h * 0.7, w, h * 0.3));
            dc.DrawEllipse(Brushes.Gold, null, new Point(w * 0.8, h * 0.2), h * 0.08, h * 0.08);
            // A red marker in the top-left corner lets tests verify orientation handling.
            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, w * 0.1, h * 0.1));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        var path = Path.Combine(folder, name);
        BitmapEncoder enc;
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
        }
        else
        {
            var opaque = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
            BitmapMetadata? meta = null;
            if (orientation is { } o)
            {
                meta = new BitmapMetadata("jpg");
                meta.SetQuery("/app1/ifd/{ushort=274}", o);
            }
            enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(opaque, null, meta, null));
        }
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    public static string Zip(string folder, string name = "Design Assets.zip")
    {
        var path = Path.Combine(folder, name);
        using var fs = File.Create(path);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        using var s = zip.CreateEntry("readme.txt").Open();
        s.Write(Encoding.UTF8.GetBytes("assets"));
        return path;
    }

    public static string Text(string folder, string name = "Notes.md")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "# Meeting notes\n\n- Ship the Downloads fan\n- Verify drag and drop into Gmail\n- Check PDF previews\n\nThe quick brown fox jumps over the lazy dog.\n");
        return path;
    }

    /// <summary>Populates a folder with one of everything, oldest first, so the fan order is deterministic.</summary>
    public static void PopulateDownloads(string folder)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>
        {
            Text(folder),
            OoxmlFixtures.Xlsx(folder),
            Image(folder, "Logo.png", 512, 512),
            Zip(folder),
            Pdf(folder, "Invoice.pdf"),
            Image(folder, "Beach Photo.jpg", 1200, 800),
            OoxmlFixtures.Pptx(folder),
            OoxmlFixtures.Docx(folder),
            Pdf(folder, "Project Brief.pdf"),
        };
        var t = DateTime.UtcNow.AddHours(-files.Count);
        foreach (var f in files)
        {
            t = t.AddMinutes(30);
            File.SetCreationTimeUtc(f, t);
            File.SetLastWriteTimeUtc(f, t);
        }
    }
}
