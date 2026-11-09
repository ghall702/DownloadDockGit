using System.Globalization;
using System.Xml.Linq;

namespace DownloadDock.Core.Previews;

/// <summary>
/// Reads the top-left region of the first worksheet of a SpreadsheetML workbook: values
/// (shared strings, inline strings, numbers, booleans, dates), bold fonts, fills and column widths.
/// </summary>
public static class XlsxParser
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const int MaxRows = 30;
    public const int MaxCols = 12;

    private sealed record CellStyle(bool Bold, Argb? Fill, Argb? FontColor, int NumFmtId, string? FormatCode);

    public static SheetModel? Parse(string path)
    {
        using var pkg = SafePackage.TryOpen(path);
        return pkg is null ? null : Parse(pkg);
    }

    public static SheetModel? Parse(SafePackage pkg)
    {
        var wbPart = pkg.ReadRelationships("").Values
            .FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal) && !r.External).Target
            ?? "xl/workbook.xml";
        var wb = pkg.ReadXml(wbPart);
        if (wb?.Root is null) return null;
        var wbRels = pkg.ReadRelationships(wbPart);

        // First visible sheet.
        var sheetEl = wb.Root.Element(S + "sheets")?.Elements(S + "sheet")
            .FirstOrDefault(s => (string?)s.Attribute("state") is null or "visible");
        if (sheetEl is null) return null;
        var sheetName = (string?)sheetEl.Attribute("name") ?? "Sheet1";
        var rid = (string?)sheetEl.Attribute(R + "id");
        if (rid is null || !wbRels.TryGetValue(rid, out var sheetRel)) return null;

        var shared = ReadSharedStrings(pkg, wbRels);
        var styles = ReadStyles(pkg, wbRels);
        var date1904 = (string?)wb.Root.Element(S + "workbookPr")?.Attribute("date1904") is "1" or "true";

        var sheet = pkg.ReadXml(sheetRel.Target);
        if (sheet?.Root is null) return null;

        var showGrid = (string?)sheet.Root.Element(S + "sheetViews")?.Element(S + "sheetView")?.Attribute("showGridLines") is not ("0" or "false");

        var widths = new Dictionary<int, double>();
        foreach (var col in sheet.Root.Element(S + "cols")?.Elements(S + "col") ?? Enumerable.Empty<XElement>())
        {
            var min = (int?)col.Attribute("min") ?? 0;
            var max = Math.Min((int?)col.Attribute("max") ?? min, MaxCols);
            var w = (double?)col.Attribute("width") ?? 8.43;
            var hidden = (string?)col.Attribute("hidden") is "1" or "true";
            for (var c = min; c <= max; c++) widths[c - 1] = hidden ? 0 : w;
        }

        var cells = new Dictionary<(int, int), SheetCell>();
        int maxRow = 0, maxCol = 0;
        var sheetData = sheet.Root.Element(S + "sheetData");
        if (sheetData is not null)
        {
            var implicitRow = 0;
            foreach (var row in sheetData.Elements(S + "row"))
            {
                var rIndex = ((int?)row.Attribute("r") ?? implicitRow + 1) - 1;
                implicitRow = rIndex + 1;
                if (rIndex >= MaxRows) break;
                var implicitCol = 0;
                foreach (var c in row.Elements(S + "c"))
                {
                    var (cr, cc) = ParseRef((string?)c.Attribute("r"), rIndex, implicitCol);
                    implicitCol = cc + 1;
                    if (cc >= MaxCols) continue;
                    var styleIdx = (int?)c.Attribute("s") ?? 0;
                    var style = styleIdx < styles.Count ? styles[styleIdx] : null;
                    var (text, isNumber) = CellText(c, shared, style, date1904);
                    if (text.Length == 0 && style?.Fill is null) continue;
                    cells[(cr, cc)] = new SheetCell(text, isNumber, style?.Bold ?? false, style?.Fill, style?.FontColor);
                    maxRow = Math.Max(maxRow, cr + 1);
                    maxCol = Math.Max(maxCol, cc + 1);
                }
            }
        }

        return new SheetModel(sheetName, Math.Max(maxRow, 1), Math.Max(maxCol, 1), cells, widths, showGrid);
    }

    internal static (int Row, int Col) ParseRef(string? reference, int fallbackRow, int fallbackCol)
    {
        if (string.IsNullOrEmpty(reference)) return (fallbackRow, fallbackCol);
        var col = 0;
        var i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
        }
        var row = int.TryParse(reference.AsSpan(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r - 1 : fallbackRow;
        return (row, col > 0 ? col - 1 : fallbackCol);
    }

    private static (string Text, bool IsNumber) CellText(XElement c, List<string> shared, CellStyle? style, bool date1904)
    {
        var t = (string?)c.Attribute("t") ?? "n";
        var v = c.Element(S + "v")?.Value;
        switch (t)
        {
            case "s":
                return (int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "", false);
            case "inlineStr":
                return (string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(x => x.Value) ?? Array.Empty<string>()), false);
            case "str":
                return (v ?? "", false);
            case "b":
                return (v == "1" ? "TRUE" : "FALSE", false);
            case "e":
                return (v ?? "#ERR", false);
            default:
                if (v is null) return ("", false);
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return (v, false);
                return (FormatNumber(number, style, date1904), true);
        }
    }

    internal static string FormatNumber(double number, int numFmtId, string? formatCode, bool date1904 = false)
        => FormatNumber(number, new CellStyle(false, null, null, numFmtId, formatCode), date1904);

    private static string FormatNumber(double number, CellStyle? style, bool date1904)
    {
        var id = style?.NumFmtId ?? 0;
        var code = style?.FormatCode;
        if (IsDateFormat(id, code))
        {
            try
            {
                var baseDate = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
                var dt = baseDate.AddDays(number);
                var hasTime = number % 1 != 0 && (code?.Contains('h', StringComparison.OrdinalIgnoreCase) ?? id is 22 or 18 or 19 or 20 or 21);
                return hasTime ? dt.ToString("g", CultureInfo.CurrentCulture) : dt.ToString("d", CultureInfo.CurrentCulture);
            }
            catch (ArgumentOutOfRangeException) { }
        }
        if (id is 9 or 10 || (code?.Contains('%') ?? false))
            return (number * 100).ToString(id == 9 ? "0" : "0.##", CultureInfo.CurrentCulture) + "%";
        if (id is 3 or 4 || (code?.Contains("#,##") ?? false))
            return number.ToString(code?.Contains(".00") == true || id == 4 ? "#,##0.00" : "#,##0", CultureInfo.CurrentCulture);
        if (code?.Contains("0.00") == true || id == 2)
            return number.ToString("0.00", CultureInfo.CurrentCulture);
        var rounded = Math.Round(number, 6);
        return rounded.ToString("0.######", CultureInfo.CurrentCulture);
    }

    internal static bool IsDateFormat(int id, string? code)
    {
        if (id is >= 14 and <= 22 or >= 45 and <= 47) return true;
        if (string.IsNullOrEmpty(code)) return false;
        // Strip quoted literals and bracketed sections ([Red], [$-409]) before looking for date tokens.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", "");
        return cleaned.IndexOfAny(new[] { 'd', 'D', 'y', 'Y' }) >= 0
               || (cleaned.Contains('m', StringComparison.OrdinalIgnoreCase) && !cleaned.Contains('0') && !cleaned.Contains('#'));
    }

    private static List<string> ReadSharedStrings(SafePackage pkg, Dictionary<string, (string Type, string Target, bool External)> wbRels)
    {
        var list = new List<string>();
        var part = wbRels.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target;
        if (part is null) return list;
        var doc = pkg.ReadXml(part);
        if (doc?.Root is null) return list;
        foreach (var si in doc.Root.Elements(S + "si"))
        {
            // Skip phonetic runs (rPh) – they're annotations, not cell text.
            list.Add(string.Concat(si.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value)));
            if (list.Count > 200_000) break;
        }
        return list;
    }

    private static List<CellStyle> ReadStyles(SafePackage pkg, Dictionary<string, (string Type, string Target, bool External)> wbRels)
    {
        var result = new List<CellStyle>();
        var part = wbRels.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal)).Target;
        if (part is null) return result;
        var doc = pkg.ReadXml(part);
        var root = doc?.Root;
        if (root is null) return result;

        var formats = root.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => f.Attribute("numFmtId") is not null)
            .GroupBy(f => (int)f.Attribute("numFmtId")!)
            .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("formatCode")) ?? new();

        var fonts = root.Element(S + "fonts")?.Elements(S + "font")
            .Select(f => (Bold: f.Element(S + "b") is { } b && (string?)b.Attribute("val") is not ("0" or "false"),
                          Color: Argb.TryParseHex((string?)f.Element(S + "color")?.Attribute("rgb"))))
            .ToList() ?? new();

        var fills = root.Element(S + "fills")?.Elements(S + "fill")
            .Select(f =>
            {
                var pf = f.Element(S + "patternFill");
                if ((string?)pf?.Attribute("patternType") is null or "none") return (Argb?)null;
                return Argb.TryParseHex((string?)pf!.Element(S + "fgColor")?.Attribute("rgb"));
            })
            .ToList() ?? new();

        foreach (var xf in root.Element(S + "cellXfs")?.Elements(S + "xf") ?? Enumerable.Empty<XElement>())
        {
            var fontId = (int?)xf.Attribute("fontId") ?? 0;
            var fillId = (int?)xf.Attribute("fillId") ?? 0;
            var numFmtId = (int?)xf.Attribute("numFmtId") ?? 0;
            var font = fontId < fonts.Count ? fonts[fontId] : (false, null);
            var fill = fillId < fills.Count ? fills[fillId] : null;
            // Treat pure-white text colour as "automatic" so it doesn't vanish on a white preview.
            var fontColor = font.Color is { } fc && fc.Value == 0xFFFFFFFF && fill is null ? null : font.Color;
            result.Add(new CellStyle(font.Bold, fill, fontColor, numFmtId, formats.TryGetValue(numFmtId, out var code) ? code : null));
            if (result.Count > 10_000) break;
        }
        return result;
    }
}
