namespace DownloadDock.Core.Previews;

/// <summary>ARGB colour (0xAARRGGBB).</summary>
public readonly record struct Argb(uint Value)
{
    public byte A => (byte)(Value >> 24);
    public byte R => (byte)(Value >> 16);
    public byte G => (byte)(Value >> 8);
    public byte B => (byte)Value;

    public static Argb FromRgb(byte r, byte g, byte b, byte a = 255) => new(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

    public static readonly Argb Black = new(0xFF000000);
    public static readonly Argb White = new(0xFFFFFFFF);

    /// <summary>Parses "RRGGBB" / "AARRGGBB" (with or without '#'). Returns null for "auto" or invalid input.</summary>
    public static Argb? TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new Argb(0xFF000000 | rgb);
        if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var argb))
            return new Argb(argb);
        return null;
    }

    public Argb WithAlpha(byte a) => new((Value & 0x00FFFFFF) | ((uint)a << 24));

    /// <summary>Relative luminance 0..1 (sRGB approximation).</summary>
    public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;

    /// <summary>Applies DrawingML lumMod / lumOff (fractions, e.g. 0.75 / 0.25) in HSL space.</summary>
    public Argb ApplyLuminance(double lumMod, double lumOff)
    {
        RgbToHsl(R, G, B, out var h, out var s, out var l);
        l = Math.Clamp(l * lumMod + lumOff, 0, 1);
        HslToRgb(h, s, l, out var r, out var g, out var b);
        return FromRgb(r, g, b, A);
    }

    /// <summary>DrawingML tint (towards white) / shade (towards black), fractions 0..1.</summary>
    public Argb ApplyTintShade(double? tint, double? shade)
    {
        double r = R, g = G, b = B;
        if (tint is { } t) { r = 255 - (255 - r) * t; g = 255 - (255 - g) * t; b = 255 - (255 - b) * t; }
        if (shade is { } sh) { r *= sh; g *= sh; b *= sh; }
        return FromRgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), A);
    }

    private static void RgbToHsl(byte rb, byte gb, byte bb, out double h, out double s, out double l)
    {
        double r = rb / 255.0, g = gb / 255.0, b = bb / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min) { h = 0; s = 0; return; }
        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
    }

    private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        if (s == 0) { r = g = b = (byte)Math.Round(l * 255); return; }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        r = (byte)Math.Round(Hue(p, q, h + 1.0 / 3) * 255);
        g = (byte)Math.Round(Hue(p, q, h) * 255);
        b = (byte)Math.Round(Hue(p, q, h - 1.0 / 3) * 255);
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}

public enum TextAlign { Left, Center, Right, Justify }

public sealed record TextRun(string Text, double SizePt, bool Bold = false, bool Italic = false, bool Underline = false, Argb? Color = null, string? Font = null);

public sealed record TextParagraph(IReadOnlyList<TextRun> Runs, TextAlign Align = TextAlign.Left, bool Bullet = false, int Level = 0, double SpaceAfterPt = 0)
{
    public string PlainText => string.Concat(Runs.Select(r => r.Text));
}

// ---------------------------------------------------------------- Word

public abstract record DocBlock;

public enum ParagraphRole { Normal, Title, Subtitle, Heading1, Heading2, Heading3, ListItem }

public sealed record DocParagraph(TextParagraph Paragraph, ParagraphRole Role) : DocBlock;

public sealed record DocImage(byte[] Data, double WidthPt, double HeightPt) : DocBlock;

public sealed record DocTable(IReadOnlyList<IReadOnlyList<string>> Rows) : DocBlock;

public sealed record DocumentModel(
    double PageWidthPt,
    double PageHeightPt,
    double MarginLeftPt,
    double MarginTopPt,
    double MarginRightPt,
    double MarginBottomPt,
    IReadOnlyList<DocBlock> Blocks);

// ---------------------------------------------------------------- PowerPoint

public abstract record Fill;
public sealed record SolidFill(Argb Color) : Fill;
public sealed record GradientFill(IReadOnlyList<(double Position, Argb Color)> Stops, double AngleDegrees) : Fill;
public sealed record ImageFill(byte[] Data) : Fill;

public enum ShapeGeometry { Rectangle, RoundRectangle, Ellipse, Line, Other }

public enum VerticalAnchor { Top, Middle, Bottom }

/// <summary>A shape on a slide. Coordinates are in EMU (914400 per inch).</summary>
public sealed record SlideShape(
    double X, double Y, double Width, double Height,
    ShapeGeometry Geometry,
    Fill? Fill,
    Argb? LineColor,
    double LineWidthEmu,
    IReadOnlyList<TextParagraph> Paragraphs,
    VerticalAnchor Anchor,
    byte[]? Image,
    IReadOnlyList<IReadOnlyList<string>>? Table = null,
    double RotationDegrees = 0);

public sealed record SlideModel(double WidthEmu, double HeightEmu, Fill? Background, IReadOnlyList<SlideShape> Shapes);

// ---------------------------------------------------------------- Excel

public sealed record SheetCell(string Text, bool IsNumber, bool Bold, Argb? Fill, Argb? FontColor);

public sealed record SheetModel(
    string SheetName,
    int RowCount,
    int ColumnCount,
    IReadOnlyDictionary<(int Row, int Col), SheetCell> Cells,
    IReadOnlyDictionary<int, double> ColumnWidthChars,
    bool ShowGridLines);
