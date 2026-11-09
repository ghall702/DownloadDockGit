using System.IO.Compression;
using DownloadDock.Core.Previews;

namespace DownloadDock.Core.Tests;

public class DocxParserTests
{
    [Fact]
    public void Parses_page_paragraph_roles_runs_images_and_tables()
    {
        using var f = new TestFolder();
        var doc = DocxParser.Parse(OoxmlFixtures.Docx(f.Path));
        Assert.NotNull(doc);

        // Letter size from twips, margins converted to points.
        Assert.Equal(612, doc!.PageWidthPt, 1);
        Assert.Equal(792, doc.PageHeightPt, 1);
        Assert.Equal(54, doc.MarginLeftPt, 1);

        var paragraphs = doc.Blocks.OfType<DocParagraph>().ToList();
        Assert.Equal(ParagraphRole.Title, paragraphs[0].Role);
        Assert.Equal("Project Brief", paragraphs[0].Paragraph.PlainText);
        Assert.Equal(TextAlign.Center, paragraphs[0].Paragraph.Align);
        Assert.Equal(28, paragraphs[0].Paragraph.Runs[0].SizePt, 1);

        Assert.Equal(ParagraphRole.Heading1, paragraphs[1].Role);
        Assert.True(paragraphs[1].Paragraph.Runs[0].Bold);
        Assert.Equal(0xFF2F5496u, paragraphs[1].Paragraph.Runs[0].Color!.Value.Value);

        var mixed = paragraphs[2].Paragraph;
        Assert.Equal("Plain bold red", mixed.PlainText);
        Assert.False(mixed.Runs[0].Bold);
        Assert.True(mixed.Runs[1].Bold && mixed.Runs[1].Italic);
        Assert.Equal(11, mixed.Runs[0].SizePt, 1); // docDefaults

        var bullet = paragraphs[3];
        Assert.Equal(ParagraphRole.ListItem, bullet.Role);
        Assert.True(bullet.Paragraph.Bullet);
        Assert.Equal(1, bullet.Paragraph.Level);

        var image = Assert.Single(doc.Blocks.OfType<DocImage>());
        Assert.Equal(100, image.WidthPt, 1);
        Assert.Equal(OoxmlFixtures.TinyPng, image.Data);

        var table = Assert.Single(doc.Blocks.OfType<DocTable>());
        Assert.Equal(new[] { "Name", "Value" }, table.Rows[0]);
    }

    [Fact]
    public void Non_zip_file_returns_null()
    {
        using var f = new TestFolder();
        Assert.Null(DocxParser.Parse(f.File("fake.docx", "not a zip")));
    }

    [Theory]
    [InlineData("Heading1", "heading 1", ParagraphRole.Heading1)]
    [InlineData("berschrift1", "heading 1", ParagraphRole.Heading1)]
    [InlineData("Titre", "Title", ParagraphRole.Title)]
    [InlineData("ListParagraph", "List Paragraph", ParagraphRole.ListItem)]
    [InlineData("Normal", "Normal", ParagraphRole.Normal)]
    public void Classifies_style_roles(string id, string name, ParagraphRole expected)
        => Assert.Equal(expected, DocxParser.ClassifyRole(id, name));
}

public class PptxParserTests
{
    [Fact]
    public void Parses_slide_size_background_shapes_text_and_pictures()
    {
        using var f = new TestFolder();
        var slide = PptxParser.Parse(OoxmlFixtures.Pptx(f.Path));
        Assert.NotNull(slide);
        Assert.Equal(12192000, slide!.WidthEmu);
        Assert.Equal(6858000, slide.HeightEmu);

        // Gradient background resolved through the theme.
        var bg = Assert.IsType<GradientFill>(slide.Background);
        Assert.Equal(0xFFE8562Au, bg.Stops[0].Color.Value);
        Assert.Equal(0xFF7030A0u, bg.Stops[1].Color.Value);
        Assert.Equal(45, bg.AngleDegrees, 1);

        // Title placeholder without xfrm inherits the master's geometry and default size.
        var title = slide.Shapes.Single(s => s.Paragraphs.Any(p => p.PlainText == "Q1 Strategy"));
        Assert.Equal(838200, title.X);
        Assert.Equal(44, title.Paragraphs[0].Runs[0].SizePt, 1);
        Assert.True(title.Paragraphs[0].Runs[0].Bold);
        Assert.Equal(0xFFFFFFFFu, title.Paragraphs[0].Runs[0].Color!.Value.Value);

        var box = slide.Shapes.Single(s => s.Paragraphs.Any(p => p.PlainText == "Revenue +12%"));
        Assert.Equal(ShapeGeometry.RoundRectangle, box.Geometry);
        Assert.Equal(VerticalAnchor.Middle, box.Anchor);
        Assert.Equal(TextAlign.Center, box.Paragraphs[0].Align);
        // bg1 (white) with lumMod 75% = #BFBFBF
        var fill = Assert.IsType<SolidFill>(box.Fill);
        Assert.Equal(0xFFBFBFBFu, fill.Color.Value);

        // Picture inside a group scaled ×2 and offset.
        var pic = slide.Shapes.Single(s => s.Image is not null);
        Assert.Equal(6096000 + 1000000, pic.X);
        Assert.Equal(1000000, pic.Width);
    }
}

public class XlsxParserTests
{
    [Fact]
    public void Reads_first_visible_sheet_with_values_styles_and_widths()
    {
        using var f = new TestFolder();
        var sheet = XlsxParser.Parse(OoxmlFixtures.Xlsx(f.Path));
        Assert.NotNull(sheet);
        Assert.Equal("Budget 2026", sheet!.SheetName);
        Assert.Equal("Item", sheet.Cells[(0, 0)].Text);
        Assert.True(sheet.Cells[(0, 0)].Bold);
        Assert.Equal(0xFFDDEBF7u, sheet.Cells[(0, 1)].Fill!.Value.Value);
        Assert.Equal("Rich text", sheet.Cells[(1, 0)].Text);
        Assert.True(sheet.Cells[(1, 1)].IsNumber);
        Assert.Equal("Inline", sheet.Cells[(2, 0)].Text);
        Assert.Equal(new DateTime(2026, 1, 1).ToString("d"), sheet.Cells[(2, 1)].Text);
        Assert.EndsWith("%", sheet.Cells[(2, 2)].Text);
        Assert.Equal("TRUE", sheet.Cells[(4, 3)].Text);
        Assert.Equal(20, sheet.ColumnWidthChars[0]);
        Assert.Equal(5, sheet.RowCount);
        Assert.Equal(4, sheet.ColumnCount);
    }

    [Theory]
    [InlineData("A1", 0, 0)]
    [InlineData("B3", 2, 1)]
    [InlineData("Z10", 9, 25)]
    [InlineData("AA1", 0, 26)]
    [InlineData("AB12", 11, 27)]
    public void Parses_cell_references(string reference, int row, int col)
        => Assert.Equal((row, col), XlsxParser.ParseRef(reference, -1, -1));

    [Theory]
    [InlineData(14, null, true)]
    [InlineData(0, null, false)]
    [InlineData(164, "dd/mm/yyyy", true)]
    [InlineData(165, "#,##0.00", false)]
    [InlineData(166, "[Red]0.00", false)]
    [InlineData(167, "\"Day\" 0", false)]
    [InlineData(168, "mmm-yy", true)]
    public void Detects_date_formats(int id, string? code, bool expected)
        => Assert.Equal(expected, XlsxParser.IsDateFormat(id, code));
}

public class EmbeddedThumbnailTests
{
    [Fact]
    public void Extracts_ooxml_thumbnail_via_relationship()
    {
        using var f = new TestFolder();
        Assert.Equal(OoxmlFixtures.TinyPng, EmbeddedThumbnail.TryExtract(OoxmlFixtures.Docx(f.Path, withThumbnail: true)));
    }

    [Fact]
    public void Extracts_odf_thumbnail()
    {
        using var f = new TestFolder();
        Assert.Equal(OoxmlFixtures.TinyPng, EmbeddedThumbnail.TryExtract(OoxmlFixtures.Odt(f.Path)));
    }

    [Fact]
    public void Returns_null_when_absent()
    {
        using var f = new TestFolder();
        Assert.Null(EmbeddedThumbnail.TryExtract(OoxmlFixtures.Xlsx(f.Path)));
    }
}

public class SafePackageTests
{
    [Theory]
    [InlineData("word", "media/image1.png", "word/media/image1.png")]
    [InlineData("ppt/slides", "../media/image1.png", "ppt/media/image1.png")]
    [InlineData("ppt/slides", "/ppt/media/x.png", "ppt/media/x.png")]
    [InlineData("a", "../../../../etc/passwd", "etc/passwd")]
    [InlineData("word", "media/my%20image.png", "word/media/my image.png")]
    public void Resolves_relationship_targets_without_escaping_the_package(string baseDir, string target, string expected)
        => Assert.Equal(expected, SafePackage.ResolvePath(baseDir, target));

    [Fact]
    public void Refuses_entries_larger_than_the_cap()
    {
        using var f = new TestFolder();
        var path = Path.Combine(f.Path, "bomb.docx");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("big.xml", CompressionLevel.Optimal).Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 40; i++) s.Write(chunk); // 40 MB of zeros compresses to ~40 KB
        }
        using var pkg = SafePackage.TryOpen(path)!;
        Assert.Null(pkg.ReadBytes("big.xml", SafePackage.MaxXmlBytes));
    }

    [Fact]
    public void Rejects_dtd_entities()
    {
        using var f = new TestFolder();
        var path = Path.Combine(f.Path, "xxe.docx");
        OoxmlFixtures.Write(path, new Dictionary<string, string>
        {
            ["a.xml"] = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>",
        });
        using var pkg = SafePackage.TryOpen(path)!;
        Assert.Null(pkg.ReadXml("a.xml"));
    }
}

public class TextPreviewReaderTests
{
    [Fact]
    public void Reads_lines_and_expands_tabs()
    {
        using var f = new TestFolder();
        var lines = TextPreviewReader.TryRead(f.File("notes.txt", "first\r\n\tsecond\nthird"));
        Assert.Equal(new[] { "first", "    second", "third" }, lines);
    }

    [Fact]
    public void Binary_content_is_rejected()
    {
        Assert.Null(TextPreviewReader.Decode(new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 }, 10, 80));
    }

    [Fact]
    public void Utf16_with_bom_is_decoded()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("héllo")).ToArray();
        Assert.Equal(new[] { "héllo" }, TextPreviewReader.Decode(bytes, 10, 80));
    }

    [Fact]
    public void Line_and_column_limits_apply()
    {
        var text = string.Join("\n", Enumerable.Range(0, 100).Select(i => new string('x', 300)));
        var lines = TextPreviewReader.Decode(System.Text.Encoding.UTF8.GetBytes(text), 5, 40)!;
        Assert.Equal(5, lines.Count);
        Assert.All(lines, l => Assert.Equal(40, l.Length));
    }
}

public class ArgbTests
{
    [Fact]
    public void Parses_hex_and_rejects_auto()
    {
        Assert.Equal(0xFF336699u, Argb.TryParseHex("336699")!.Value.Value);
        Assert.Equal(0x80336699u, Argb.TryParseHex("#80336699")!.Value.Value);
        Assert.Null(Argb.TryParseHex("auto"));
        Assert.Null(Argb.TryParseHex(null));
    }

    [Fact]
    public void Luminance_modifiers_match_office()
    {
        // White at lumMod 50% → mid grey; black with lumOff 50% → mid grey.
        Assert.Equal(0xFF808080u, Argb.White.ApplyLuminance(0.5, 0).Value);
        Assert.Equal(0xFF808080u, Argb.Black.ApplyLuminance(1, 0.5).Value);
    }

    [Fact]
    public void File_kinds_are_classified()
    {
        Assert.Equal(PreviewKind.Pdf, FileKinds.Classify("a.PDF"));
        Assert.Equal(PreviewKind.Image, FileKinds.Classify("a.webp"));
        Assert.Equal(PreviewKind.Presentation, FileKinds.Classify("a.pptx"));
        Assert.Equal(PreviewKind.Spreadsheet, FileKinds.Classify("a.xlsx"));
        Assert.Equal(PreviewKind.WordDocument, FileKinds.Classify("a.docx"));
        Assert.Equal(PreviewKind.Other, FileKinds.Classify("a.zip"));
        Assert.Equal(PreviewKind.Other, FileKinds.Classify("noext"));
    }
}
