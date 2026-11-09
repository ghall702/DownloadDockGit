using System.IO;
using DownloadDock.Core.Previews;
using DownloadDock.Core.Tests;
using DownloadDock.Thumbnails;

namespace DownloadDock.Windows.Tests;

public class ThumbnailTests
{
    [Fact]
    public void Pdf_first_page_is_rendered_by_pdfium()
    {
        using var f = new TestFolder();
        var pdf = SampleFiles.Pdf(f.Path);
        Sta.Run(() =>
        {
            var bmp = PdfThumbnailer.Render(pdf, 200);
            Assert.NotNull(bmp);
            Assert.Equal(200, bmp!.PixelHeight);
            Assert.Equal(155, bmp.PixelWidth); // 612×792 page aspect
            var centre = Pixels.At(bmp, bmp.PixelWidth / 2, bmp.PixelHeight / 2);
            Assert.True(centre.R > 180 && centre.G < 80 && centre.B < 80, $"centre should be the red block, got {centre}");
            var corner = Pixels.At(bmp, 2, bmp.PixelHeight - 3);
            Assert.True(corner.R > 240 && corner.G > 240 && corner.B > 240, $"margin should be white, got {corner}");
        });
    }

    [Fact]
    public void Landscape_pdf_keeps_its_aspect()
    {
        using var f = new TestFolder();
        var pdf = SampleFiles.Pdf(f.Path, "wide.pdf", 842, 595);
        Sta.Run(() =>
        {
            var bmp = PdfThumbnailer.Render(pdf, 200)!;
            Assert.Equal(200, bmp.PixelWidth);
            Assert.Equal(141, bmp.PixelHeight);
        });
    }

    [Fact]
    public void Corrupt_pdf_does_not_crash_and_falls_back_to_an_icon()
    {
        using var f = new TestFolder();
        var bad = f.File("broken.pdf", "%PDF-1.7 this is not really a pdf");
        Sta.Run(() =>
        {
            Assert.Null(PdfThumbnailer.Render(bad, 128));
            var r = ThumbnailRenderer.Render(bad, 128);
            Assert.NotNull(r);
            Assert.True(r!.IsIcon);
        });
    }

    [Fact]
    public void Images_are_decoded_at_reduced_size()
    {
        using var f = new TestFolder();
        var png = Sta.Run(() => SampleFiles.Image(f.Path, "photo.png", 800, 400));
        Sta.Run(() =>
        {
            var bmp = ImageThumbnailer.Render(png, 100)!;
            Assert.Equal(100, bmp.PixelWidth);
            Assert.Equal(50, bmp.PixelHeight);
            Assert.True(bmp.IsFrozen);
        });
    }

    [Fact]
    public void Exif_orientation_is_applied()
    {
        using var f = new TestFolder();
        // Stored landscape 800×400 with orientation 6 ("rotate 90° CW to display") → displayed portrait.
        var jpg = Sta.Run(() => SampleFiles.Image(f.Path, "rotated.jpg", 800, 400, orientation: 6));
        Sta.Run(() =>
        {
            var bmp = ImageThumbnailer.Render(jpg, 100)!;
            Assert.Equal(50, bmp.PixelWidth);
            Assert.Equal(100, bmp.PixelHeight);
            // The red marker (stored top-left) must now be top-right.
            var topRight = Pixels.At(bmp, bmp.PixelWidth - 2, 2);
            Assert.True(topRight.R > 180 && topRight.G < 90, $"expected red marker top-right, got {topRight}");
        });
    }

    [Fact]
    public void Office_documents_get_content_previews_without_office()
    {
        using var f = new TestFolder();
        var docx = OoxmlFixtures.Docx(f.Path);
        var pptx = OoxmlFixtures.Pptx(f.Path);
        var xlsx = OoxmlFixtures.Xlsx(f.Path);
        Sta.Run(() =>
        {
            foreach (var path in new[] { docx, pptx, xlsx })
            {
                var r = ThumbnailRenderer.Render(path, 160);
                Assert.NotNull(r);
                Assert.False(r!.IsIcon, $"{Path.GetFileName(path)} fell back to an icon ({r.Source})");
                Assert.True(Pixels.DistinctColours(r.Image) > 8, $"{Path.GetFileName(path)} preview looks blank");
            }
        });
    }

    [Fact]
    public void Built_in_renderers_draw_the_documents()
    {
        using var f = new TestFolder();
        var doc = DocxParser.Parse(OoxmlFixtures.Docx(f.Path))!;
        var slide = PptxParser.Parse(OoxmlFixtures.Pptx(f.Path))!;
        var sheet = XlsxParser.Parse(OoxmlFixtures.Xlsx(f.Path))!;
        Sta.Run(() =>
        {
            var page = DocumentPreviewRenderer.RenderDocument(doc, 200);
            Assert.Equal(200, page.PixelHeight);           // portrait letter page
            Assert.True(page.PixelWidth < page.PixelHeight);

            var s = DocumentPreviewRenderer.RenderSlide(slide, 200);
            Assert.Equal(200, s.PixelWidth);              // 16:9 slide
            Assert.Equal(113, s.PixelHeight);
            var bg = Pixels.At(s, 3, 3);                   // gradient starts at accent1 (orange)
            Assert.True(bg.R > 150 && bg.B < 120, $"slide background should be the theme gradient, got {bg}");

            var x = DocumentPreviewRenderer.RenderSheet(sheet, 200);
            Assert.Equal(200, x.PixelWidth);
            Assert.True(Pixels.DistinctColours(x) > 4);
        });
    }

    [Fact]
    public void Text_files_render_as_pages()
    {
        using var f = new TestFolder();
        var md = SampleFiles.Text(f.Path);
        Sta.Run(() =>
        {
            var r = ThumbnailRenderer.Render(md, 160)!;
            Assert.False(r.IsIcon);
            Assert.True(Pixels.DistinctColours(r.Image) > 4);
        });
    }

    [Fact]
    public void Unknown_formats_use_the_shell_icon()
    {
        using var f = new TestFolder();
        var zip = SampleFiles.Zip(f.Path);
        Sta.Run(() =>
        {
            var r = ThumbnailRenderer.Render(zip, 128)!;
            Assert.NotNull(r.Image);
            Assert.True(r.IsIcon);
        });
    }

    [Fact]
    public async Task Service_caches_in_memory_and_on_disk()
    {
        using var f = new TestFolder();
        var pdf = SampleFiles.Pdf(f.Path);
        var cacheDir = Path.Combine(f.Path, "cache");
        var info = new FileInfo(pdf);
        var entry = new DownloadDock.Core.Downloads.DownloadEntry(pdf, info.Name, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);

        using (var svc = new ThumbnailService(cacheDir))
        {
            var first = await svc.GetAsync(entry, 128);
            Assert.Equal("pdfium", first!.Source);
            Assert.Same(first, svc.TryGetCached(entry, 128));
        }
        Assert.NotEmpty(Directory.GetFiles(cacheDir, "*.png"));
        using (var svc = new ThumbnailService(cacheDir))
        {
            var again = await svc.GetAsync(entry, 128);
            Assert.Equal("disk-cache", again!.Source);
        }
    }

    [Fact]
    public async Task Concurrent_requests_are_deduplicated()
    {
        using var f = new TestFolder();
        var pdf = SampleFiles.Pdf(f.Path);
        var info = new FileInfo(pdf);
        var entry = new DownloadDock.Core.Downloads.DownloadEntry(pdf, info.Name, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        using var svc = new ThumbnailService(Path.Combine(f.Path, "cache"));
        var tasks = Enumerable.Range(0, 10).Select(_ => svc.GetAsync(entry, 96)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Same(results[0], r));
    }
}
