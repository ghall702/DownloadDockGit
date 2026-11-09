using System.IO;
using System.IO.Compression;
using System.Text;

namespace DownloadDock.Core.Tests;

/// <summary>Builds small but structurally valid OOXML / ODF packages for parser tests.</summary>
public static class OoxmlFixtures
{
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGO8o6Hxn4GBgYGJAQoAIZwCL1Iwrf4AAAAASUVORK5CYII=");

    public static void Write(string path, IDictionary<string, string> parts, IDictionary<string, byte[]>? binary = null)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var s = zip.CreateEntry(name).Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes);
        }
        foreach (var (name, content) in binary ?? new Dictionary<string, byte[]>())
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(content);
        }
    }

    private const string Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    public static string Docx(string folder, bool withThumbnail = false)
    {
        var path = Path.Combine(folder, "Report.docx");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["_rels/.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"{OfficeDoc}\" Target=\"word/document.xml\"/>"
                + (withThumbnail ? $"<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail\" Target=\"docProps/thumbnail.png\"/>" : "")
                + "</Relationships>",
            ["word/_rels/document.xml.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rIdS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rIdImg\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image1.png\"/></Relationships>",
            ["word/styles.xml"] = """
                <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="22"/></w:rPr></w:rPrDefault></w:docDefaults>
                  <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:rPr><w:b/><w:color w:val="2F5496"/><w:sz w:val="32"/></w:rPr></w:style>
                  <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:rPr><w:sz w:val="56"/></w:rPr></w:style>
                </w:styles>
                """,
            ["word/document.xml"] = """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                            xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                            xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <w:body>
                    <w:p><w:pPr><w:pStyle w:val="Title"/><w:jc w:val="center"/></w:pPr><w:r><w:t>Project Brief</w:t></w:r></w:p>
                    <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Overview</w:t></w:r></w:p>
                    <w:p><w:r><w:t xml:space="preserve">Plain </w:t></w:r><w:r><w:rPr><w:b/><w:i/><w:color w:val="FF0000"/></w:rPr><w:t>bold red</w:t></w:r></w:p>
                    <w:p><w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Bullet item</w:t></w:r></w:p>
                    <w:p><w:r><w:drawing><wp:inline><wp:extent cx="1270000" cy="635000"/><a:graphic><a:graphicData><a:blip r:embed="rIdImg"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
                    <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Name</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Value</w:t></w:r></w:p></w:tc></w:tr>
                           <w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>1</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                    <w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1080" w:bottom="1440" w:left="1080"/></w:sectPr>
                  </w:body>
                </w:document>
                """,
        };
        var binary = new Dictionary<string, byte[]> { ["word/media/image1.png"] = TinyPng };
        if (withThumbnail) binary["docProps/thumbnail.png"] = TinyPng;
        Write(path, parts, binary);
        return path;
    }

    public static string Pptx(string folder)
    {
        var path = Path.Combine(folder, "Presentation.pptx");
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"{OfficeDoc}\" Target=\"ppt/presentation.xml\"/></Relationships>",
            ["ppt/presentation.xml"] = """
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst>
                  <p:sldSz cx="12192000" cy="6858000"/>
                </p:presentation>
                """,
            ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>",
            ["ppt/slides/_rels/slide1.xml.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rIdL\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rIdI\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image1.png\"/></Relationships>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rIdM\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rIdT\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/></Relationships>",
            ["ppt/theme/theme1.xml"] = """
                <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:themeElements><a:clrScheme name="t">
                  <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
                  <a:dk2><a:srgbClr val="1F2A44"/></a:dk2><a:lt2><a:srgbClr val="EEEEEE"/></a:lt2>
                  <a:accent1><a:srgbClr val="E8562A"/></a:accent1><a:accent2><a:srgbClr val="7030A0"/></a:accent2>
                </a:clrScheme></a:themeElements></a:theme>
                """,
            ["ppt/slideMasters/slideMaster1.xml"] = """
                <p:sldMaster xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill></p:bgPr></p:bg><p:spTree>
                    <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                      <p:spPr><a:xfrm><a:off x="838200" y="365125"/><a:ext cx="10515600" cy="1325563"/></a:xfrm></p:spPr></p:sp>
                  </p:spTree></p:cSld>
                  <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2"/>
                  <p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz="4400"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill></a:defRPr></a:lvl1pPr></p:titleStyle>
                    <p:bodyStyle><a:lvl1pPr><a:defRPr sz="2800"/></a:lvl1pPr></p:bodyStyle></p:txStyles>
                </p:sldMaster>
                """,
            ["ppt/slideLayouts/slideLayout1.xml"] = """
                <p:sldLayout xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:spTree/></p:cSld>
                </p:sldLayout>
                """,
            ["ppt/slides/slide1.xml"] = """
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:cSld>
                    <p:bg><p:bgPr><a:gradFill><a:gsLst><a:gs pos="0"><a:schemeClr val="accent1"/></a:gs><a:gs pos="100000"><a:schemeClr val="accent2"/></a:gs></a:gsLst><a:lin ang="2700000"/></a:gradFill></p:bgPr></p:bg>
                    <p:spTree>
                      <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title 1"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:spPr/>
                        <p:txBody><a:bodyPr/><a:p><a:r><a:rPr lang="en-US" b="1"><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill></a:rPr><a:t>Q1 Strategy</a:t></a:r></a:p></p:txBody></p:sp>
                      <p:sp><p:nvSpPr><p:cNvPr id="3" name="Box"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                        <p:spPr><a:xfrm><a:off x="914400" y="2743200"/><a:ext cx="3657600" cy="914400"/></a:xfrm><a:prstGeom prst="roundRect"/><a:solidFill><a:schemeClr val="bg1"><a:lumMod val="75000"/></a:schemeClr></a:solidFill></p:spPr>
                        <p:txBody><a:bodyPr anchor="ctr"/><a:p><a:pPr algn="ctr"/><a:r><a:rPr sz="2000"/><a:t>Revenue +12%</a:t></a:r></a:p></p:txBody></p:sp>
                      <p:grpSp><p:grpSpPr><a:xfrm><a:off x="6096000" y="2743200"/><a:ext cx="2000000" cy="2000000"/><a:chOff x="0" y="0"/><a:chExt cx="1000000" cy="1000000"/></a:xfrm></p:grpSpPr>
                        <p:pic><p:nvPicPr><p:cNvPr id="4" name="Pic"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rIdI"/></p:blipFill>
                          <p:spPr><a:xfrm><a:off x="500000" y="0"/><a:ext cx="500000" cy="500000"/></a:xfrm></p:spPr></p:pic>
                      </p:grpSp>
                    </p:spTree>
                  </p:cSld>
                </p:sld>
                """,
        };
        Write(path, parts, new Dictionary<string, byte[]> { ["ppt/media/image1.png"] = TinyPng });
        return path;
    }

    public static string Xlsx(string folder)
    {
        var path = Path.Combine(folder, "Budget.xlsx");
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"{OfficeDoc}\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Hidden" sheetId="2" state="hidden" r:id="rId9"/><sheet name="Budget 2026" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>",
            ["xl/sharedStrings.xml"] = """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Item</t></si><si><t>Cost</t></si><si><r><t>Rich </t></r><r><t>text</t></r></si></sst>
                """,
            ["xl/styles.xml"] = """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/></numFmts>
                  <fonts count="2"><font><sz val="11"/></font><font><b/><sz val="11"/></font></fonts>
                  <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDDEBF7"/></patternFill></fill></fills>
                  <cellXfs count="4"><xf numFmtId="0" fontId="0" fillId="0"/><xf numFmtId="0" fontId="1" fillId="2"/><xf numFmtId="164" fontId="0" fillId="0"/><xf numFmtId="9" fontId="0" fillId="0"/></cellXfs>
                </styleSheet>
                """,
            ["xl/worksheets/sheet1.xml"] = """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <cols><col min="1" max="1" width="20"/></cols>
                  <sheetData>
                    <row r="1"><c r="A1" t="s" s="1"><v>0</v></c><c r="B1" t="s" s="1"><v>1</v></c></row>
                    <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>1234.5</v></c></row>
                    <row r="3"><c r="A3" t="inlineStr"><is><t>Inline</t></is></c><c r="B3" s="2"><v>46023</v></c><c r="C3" s="3"><v>0.25</v></c></row>
                    <row r="5"><c r="D5" t="b"><v>1</v></c></row>
                  </sheetData>
                </worksheet>
                """,
        };
        Write(path, parts);
        return path;
    }

    public static string Odt(string folder)
    {
        var path = Path.Combine(folder, "Letter.odt");
        Write(path, new Dictionary<string, string> { ["mimetype"] = "application/vnd.oasis.opendocument.text" },
            new Dictionary<string, byte[]> { ["Thumbnails/thumbnail.png"] = TinyPng });
        return path;
    }
}
