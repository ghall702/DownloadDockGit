using System.Globalization;
using System.Xml.Linq;

namespace DownloadDock.Core.Previews;

/// <summary>
/// Extracts enough of a WordprocessingML document to draw a faithful-looking first page:
/// page size and margins, paragraphs with heading/list roles, run formatting, inline images
/// and simple tables. Read-only; never touches macros or external content.
/// </summary>
public static class DocxParser
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

    /// <summary>Maximum blocks parsed – far more than fit on one page.</summary>
    public const int MaxBlocks = 160;

    private sealed record StyleInfo(string? Name, string? BasedOn, double? SizePt, bool? Bold, bool? Italic, Argb? Color, TextAlign? Align);

    public static DocumentModel? Parse(string path)
    {
        using var pkg = SafePackage.TryOpen(path);
        return pkg is null ? null : Parse(pkg);
    }

    public static DocumentModel? Parse(SafePackage pkg)
    {
        var mainPart = FindMainPart(pkg);
        if (mainPart is null) return null;
        var doc = pkg.ReadXml(mainPart);
        var body = doc?.Root?.Element(W + "body");
        if (body is null) return null;

        var rels = pkg.ReadRelationships(mainPart);
        var styles = ReadStyles(pkg, rels, out var defaultSize);

        // Page geometry (twips → points). Default: A4 portrait with 1" margins.
        double pw = 595, ph = 842, ml = 72, mt = 72, mr = 72, mb = 72;
        var sect = body.Elements(W + "sectPr").LastOrDefault() ?? body.Descendants(W + "sectPr").FirstOrDefault();
        if (sect is not null)
        {
            var pgSz = sect.Element(W + "pgSz");
            pw = Twips(pgSz, "w") ?? pw;
            ph = Twips(pgSz, "h") ?? ph;
            var mar = sect.Element(W + "pgMar");
            ml = Twips(mar, "left") ?? Twips(mar, "start") ?? ml;
            mr = Twips(mar, "right") ?? Twips(mar, "end") ?? mr;
            mt = Math.Abs(Twips(mar, "top") ?? mt);
            mb = Math.Abs(Twips(mar, "bottom") ?? mb);
        }

        var blocks = new List<DocBlock>();
        foreach (var el in body.Elements())
        {
            if (blocks.Count >= MaxBlocks) break;
            if (el.Name == W + "p")
            {
                ParseParagraph(pkg, rels, styles, defaultSize, el, blocks);
            }
            else if (el.Name == W + "tbl")
            {
                var rows = new List<IReadOnlyList<string>>();
                foreach (var tr in el.Elements(W + "tr").Take(30))
                {
                    var cells = tr.Elements(W + "tc")
                        .Select(tc => string.Join(" ", tc.Descendants(W + "p").Select(ParagraphText)).Trim())
                        .Take(12)
                        .ToList();
                    rows.Add(cells);
                }
                if (rows.Count > 0) blocks.Add(new DocTable(rows));
            }
            else if (el.Name == W + "sdt")
            {
                // Content controls (e.g. cover pages / TOCs) – flatten their paragraphs.
                foreach (var p in el.Descendants(W + "p").Take(40))
                    ParseParagraph(pkg, rels, styles, defaultSize, p, blocks);
            }
        }

        return new DocumentModel(pw, ph, ml, mt, mr, mb, blocks);
    }

    private static string? FindMainPart(SafePackage pkg)
    {
        foreach (var rel in pkg.ReadRelationships("").Values)
        {
            if (rel.Type.EndsWith("/officeDocument", StringComparison.Ordinal) && !rel.External)
                return rel.Target;
        }
        return pkg.Exists("word/document.xml") ? "word/document.xml" : null;
    }

    private static void ParseParagraph(SafePackage pkg, Dictionary<string, (string Type, string Target, bool External)> rels,
        Dictionary<string, StyleInfo> styles, double defaultSize, XElement p, List<DocBlock> blocks)
    {
        var pPr = p.Element(W + "pPr");
        var styleId = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val");
        var style = Resolve(styles, styleId);
        var role = ClassifyRole(styleId, style.Name);
        var isList = pPr?.Element(W + "numPr") is not null || role == ParagraphRole.ListItem;
        if (isList && role == ParagraphRole.Normal) role = ParagraphRole.ListItem;
        var level = (int?)pPr?.Element(W + "numPr")?.Element(W + "ilvl")?.Attribute(W + "val") ?? 0;
        var align = ParseAlign((string?)pPr?.Element(W + "jc")?.Attribute(W + "val")) ?? style.Align ?? TextAlign.Left;
        var spaceAfter = ((double?)pPr?.Element(W + "spacing")?.Attribute(W + "after") ?? 160) / 20.0;

        var runs = new List<TextRun>();
        foreach (var r in p.Descendants(W + "r"))
        {
            // Images inside the run become their own block (after any text collected so far).
            foreach (var blip in r.Descendants(A + "blip"))
            {
                var embed = (string?)blip.Attribute(R + "embed");
                if (embed is null || !rels.TryGetValue(embed, out var rel) || rel.External) continue;
                var data = pkg.ReadBytes(rel.Target, SafePackage.MaxImageBytes);
                if (data is null) continue;
                var extent = r.Descendants(WP + "extent").FirstOrDefault();
                var cx = (double?)extent?.Attribute("cx") ?? 914400 * 3;
                var cy = (double?)extent?.Attribute("cy") ?? 914400 * 2;
                if (runs.Count > 0)
                {
                    blocks.Add(new DocParagraph(new TextParagraph(runs.ToList(), align, isList, level, spaceAfter), role));
                    runs.Clear();
                }
                blocks.Add(new DocImage(data, cx / 12700.0, cy / 12700.0));
            }

            var text = RunText(r);
            if (text.Length == 0) continue;
            var rPr = r.Element(W + "rPr");
            var size = HalfPoints(rPr?.Element(W + "sz")) ?? style.SizePt ?? DefaultRoleSize(role, defaultSize);
            var bold = OnOff(rPr?.Element(W + "b")) ?? style.Bold ?? role is ParagraphRole.Title or ParagraphRole.Heading1 or ParagraphRole.Heading2 or ParagraphRole.Heading3;
            var italic = OnOff(rPr?.Element(W + "i")) ?? style.Italic ?? false;
            var underline = rPr?.Element(W + "u") is { } u && (string?)u.Attribute(W + "val") is not ("none" or null);
            var color = Argb.TryParseHex((string?)rPr?.Element(W + "color")?.Attribute(W + "val")) ?? style.Color;
            var font = (string?)rPr?.Element(W + "rFonts")?.Attribute(W + "ascii");
            runs.Add(new TextRun(text, size, bold, italic, underline, color, font));
        }

        // Empty paragraphs still take vertical space on the page.
        if (runs.Count == 0)
            runs.Add(new TextRun("", style.SizePt ?? defaultSize));
        blocks.Add(new DocParagraph(new TextParagraph(runs, align, isList, level, spaceAfter), role));
    }

    private static string RunText(XElement r)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var n in r.Elements())
        {
            if (n.Name == W + "t") sb.Append(n.Value);
            else if (n.Name == W + "tab") sb.Append("    ");
            else if (n.Name == W + "br" || n.Name == W + "cr") sb.Append('\n');
            else if (n.Name == W + "noBreakHyphen") sb.Append('-');
        }
        return sb.ToString();
    }

    private static string ParagraphText(XElement p) => string.Concat(p.Descendants(W + "r").Select(RunText));

    private static Dictionary<string, StyleInfo> ReadStyles(SafePackage pkg, Dictionary<string, (string Type, string Target, bool External)> rels, out double defaultSize)
    {
        defaultSize = 11;
        var map = new Dictionary<string, StyleInfo>(StringComparer.OrdinalIgnoreCase);
        var stylesPart = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal)).Target ?? "word/styles.xml";
        var doc = pkg.ReadXml(stylesPart);
        if (doc?.Root is null) return map;

        var defSz = HalfPoints(doc.Root.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr")?.Element(W + "sz"));
        if (defSz is { } d) defaultSize = d;

        foreach (var s in doc.Root.Elements(W + "style"))
        {
            var id = (string?)s.Attribute(W + "styleId");
            if (id is null) continue;
            var rPr = s.Element(W + "rPr");
            map[id] = new StyleInfo(
                (string?)s.Element(W + "name")?.Attribute(W + "val"),
                (string?)s.Element(W + "basedOn")?.Attribute(W + "val"),
                HalfPoints(rPr?.Element(W + "sz")),
                OnOff(rPr?.Element(W + "b")),
                OnOff(rPr?.Element(W + "i")),
                Argb.TryParseHex((string?)rPr?.Element(W + "color")?.Attribute(W + "val")),
                ParseAlign((string?)s.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val")));
        }
        return map;
    }

    /// <summary>Merges a style with its basedOn chain (bounded to avoid cycles).</summary>
    private static StyleInfo Resolve(Dictionary<string, StyleInfo> styles, string? id)
    {
        var result = new StyleInfo(null, null, null, null, null, null, null);
        var depth = 0;
        while (id is not null && depth++ < 8 && styles.TryGetValue(id, out var s))
        {
            result = new StyleInfo(
                result.Name ?? s.Name,
                null,
                result.SizePt ?? s.SizePt,
                result.Bold ?? s.Bold,
                result.Italic ?? s.Italic,
                result.Color ?? s.Color,
                result.Align ?? s.Align);
            id = s.BasedOn;
        }
        return result;
    }

    internal static ParagraphRole ClassifyRole(string? styleId, string? styleName)
    {
        var n = (styleName ?? styleId ?? "").ToLowerInvariant().Replace(" ", "");
        if (n.Length == 0) return ParagraphRole.Normal;
        if (n == "title") return ParagraphRole.Title;
        if (n == "subtitle") return ParagraphRole.Subtitle;
        if (n.StartsWith("heading1") || n == "berschrift1" || n.EndsWith("heading1")) return ParagraphRole.Heading1;
        if (n.StartsWith("heading2") || n.EndsWith("heading2")) return ParagraphRole.Heading2;
        if (n.StartsWith("heading") || n.StartsWith("berschrift")) return ParagraphRole.Heading3;
        if (n.StartsWith("listparagraph") || n.StartsWith("listbullet") || n.StartsWith("listnumber")) return ParagraphRole.ListItem;
        return ParagraphRole.Normal;
    }

    private static double DefaultRoleSize(ParagraphRole role, double normal) => role switch
    {
        ParagraphRole.Title => 28,
        ParagraphRole.Subtitle => 15,
        ParagraphRole.Heading1 => 16,
        ParagraphRole.Heading2 => 13,
        ParagraphRole.Heading3 => 12,
        _ => normal,
    };

    private static TextAlign? ParseAlign(string? v) => v switch
    {
        "center" => TextAlign.Center,
        "right" or "end" => TextAlign.Right,
        "both" or "distribute" => TextAlign.Justify,
        "left" or "start" => TextAlign.Left,
        _ => null,
    };

    private static double? Twips(XElement? el, string attr)
        => double.TryParse((string?)el?.Attribute(W + attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 20.0 : null;

    private static double? HalfPoints(XElement? sz)
        => double.TryParse((string?)sz?.Attribute(W + "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 2.0 : null;

    /// <summary>OOXML on/off property: present without val = on; val "0"/"false"/"off" = off.</summary>
    private static bool? OnOff(XElement? el)
    {
        if (el is null) return null;
        var v = (string?)el.Attribute(W + "val");
        return v is null || !(v is "0" or "false" or "off");
    }
}
