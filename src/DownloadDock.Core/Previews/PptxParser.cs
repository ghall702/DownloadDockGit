using System.Globalization;
using System.Xml.Linq;

namespace DownloadDock.Core.Previews;

/// <summary>
/// Extracts the first slide of a PresentationML file: slide size, background (slide → layout →
/// master), shapes with fills, text (with master text-style defaults), pictures, simple tables and
/// group transforms. Placeholder positions are inherited from the layout/master as PowerPoint does.
/// </summary>
public static class PptxParser
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const double DefaultWidth = 12192000;  // 13.333" (16:9)
    private const double DefaultHeight = 6858000;  // 7.5"

    private sealed class Context
    {
        public required SafePackage Pkg { get; init; }
        public Dictionary<string, Argb> Theme { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ColorMap { get; } = new(StringComparer.Ordinal);
        public (double Size, Argb? Color) TitleStyle { get; set; } = (44, null);
        public (double Size, Argb? Color) BodyStyle { get; set; } = (28, null);
        public (double Size, Argb? Color) OtherStyle { get; set; } = (18, null);
    }

    private readonly record struct Transform(double OffX, double OffY, double ScaleX, double ScaleY)
    {
        public static readonly Transform Identity = new(0, 0, 1, 1);
        public (double X, double Y, double W, double H) Apply(double x, double y, double w, double h)
            => (OffX + x * ScaleX, OffY + y * ScaleY, w * ScaleX, h * ScaleY);
    }

    public static SlideModel? Parse(string path)
    {
        using var pkg = SafePackage.TryOpen(path);
        return pkg is null ? null : Parse(pkg);
    }

    public static SlideModel? Parse(SafePackage pkg)
    {
        var presPart = pkg.ReadRelationships("").Values
            .FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal) && !r.External).Target
            ?? "ppt/presentation.xml";
        var pres = pkg.ReadXml(presPart);
        if (pres?.Root is null) return null;

        var sldSz = pres.Root.Element(P + "sldSz");
        var width = (double?)sldSz?.Attribute("cx") ?? DefaultWidth;
        var height = (double?)sldSz?.Attribute("cy") ?? DefaultHeight;

        var presRels = pkg.ReadRelationships(presPart);
        var firstId = (string?)pres.Root.Element(P + "sldIdLst")?.Elements(P + "sldId").FirstOrDefault()?.Attribute(R + "id");
        if (firstId is null || !presRels.TryGetValue(firstId, out var slideRel)) return new SlideModel(width, height, new SolidFill(Argb.White), Array.Empty<SlideShape>());

        var slidePart = slideRel.Target;
        var slide = pkg.ReadXml(slidePart);
        if (slide?.Root is null) return null;
        var slideRels = pkg.ReadRelationships(slidePart);

        // Layout and master (for backgrounds, placeholder geometry and text defaults).
        var layoutPart = slideRels.Values.FirstOrDefault(r => r.Type.EndsWith("/slideLayout", StringComparison.Ordinal)).Target;
        var layout = layoutPart is null ? null : pkg.ReadXml(layoutPart);
        var layoutRels = layoutPart is null ? new() : pkg.ReadRelationships(layoutPart);
        var masterPart = layoutRels.Values.FirstOrDefault(r => r.Type.EndsWith("/slideMaster", StringComparison.Ordinal)).Target;
        var master = masterPart is null ? null : pkg.ReadXml(masterPart);
        var masterRels = masterPart is null ? new() : pkg.ReadRelationships(masterPart);

        var ctx = new Context { Pkg = pkg };
        var themePart = masterRels.Values.FirstOrDefault(r => r.Type.EndsWith("/theme", StringComparison.Ordinal)).Target;
        if (themePart is not null) ReadTheme(pkg.ReadXml(themePart), ctx);
        ReadColorMap(master, ctx);
        ReadTextStyles(master, ctx);

        // Background: slide → layout → master.
        var bg = ParseBackground(slide.Root, slideRels, ctx)
                 ?? ParseBackground(layout?.Root, layoutRels, ctx)
                 ?? ParseBackground(master?.Root, masterRels, ctx)
                 ?? new SolidFill(ResolveScheme("bg1", ctx) ?? Argb.White);

        var shapes = new List<SlideShape>();
        var layoutPh = CollectPlaceholders(layout?.Root);
        var masterPh = CollectPlaceholders(master?.Root);

        // Non-placeholder decorative shapes from layout and master are part of what the slide shows.
        if ((string?)slide.Root.Attribute("showMasterSp") is not ("0" or "false"))
        {
            if (master?.Root is not null) WalkTree(master.Root.Element(P + "cSld")?.Element(P + "spTree"), Transform.Identity, masterRels, ctx, null, null, shapes, skipPlaceholders: true);
            if (layout?.Root is not null) WalkTree(layout.Root.Element(P + "cSld")?.Element(P + "spTree"), Transform.Identity, layoutRels, ctx, null, null, shapes, skipPlaceholders: true);
        }
        WalkTree(slide.Root.Element(P + "cSld")?.Element(P + "spTree"), Transform.Identity, slideRels, ctx, layoutPh, masterPh, shapes, skipPlaceholders: false);

        return new SlideModel(width, height, bg, shapes);
    }

    // ------------------------------------------------------------------ shapes

    private static void WalkTree(XElement? tree, Transform t, Dictionary<string, (string Type, string Target, bool External)> rels, Context ctx,
        Dictionary<string, XElement>? layoutPh, Dictionary<string, XElement>? masterPh, List<SlideShape> output, bool skipPlaceholders)
    {
        if (tree is null) return;
        foreach (var el in tree.Elements())
        {
            if (output.Count > 200) return;
            var local = el.Name.LocalName;
            if (local == "grpSp")
            {
                var xfrm = el.Element(P + "grpSpPr")?.Element(A + "xfrm");
                var child = t;
                if (xfrm is not null)
                {
                    var (ox, oy) = Point(xfrm.Element(A + "off"));
                    var (ex, ey) = Size(xfrm.Element(A + "ext"));
                    var (cox, coy) = Point(xfrm.Element(A + "chOff"));
                    var (cex, cey) = Size(xfrm.Element(A + "chExt"));
                    var sx = cex > 0 ? ex / cex : 1;
                    var sy = cey > 0 ? ey / cey : 1;
                    // child = t ∘ (translate(off) ∘ scale(s) ∘ translate(-chOff))
                    child = new Transform(t.OffX + (ox - cox * sx) * t.ScaleX, t.OffY + (oy - coy * sy) * t.ScaleY, t.ScaleX * sx, t.ScaleY * sy);
                }
                WalkTree(el, child, rels, ctx, layoutPh, masterPh, output, skipPlaceholders);
            }
            else if (local is "sp" or "cxnSp")
            {
                var ph = el.Descendants(P + "ph").FirstOrDefault();
                if (ph is not null && skipPlaceholders) continue;
                var shape = ParseShape(el, t, rels, ctx, ph, layoutPh, masterPh);
                if (shape is not null) output.Add(shape);
            }
            else if (local == "pic")
            {
                var xfrm = el.Element(P + "spPr")?.Element(A + "xfrm");
                var ph = el.Descendants(P + "ph").FirstOrDefault();
                xfrm ??= InheritedXfrm(ph, layoutPh, masterPh);
                if (xfrm is null) continue;
                var (x, y, w, h) = Geometry(xfrm, t);
                var blip = el.Descendants(A + "blip").FirstOrDefault();
                var img = ReadBlip(blip, rels, ctx);
                if (img is null) continue;
                output.Add(new SlideShape(x, y, w, h, ShapeGeometry.Rectangle, null, null, 0, Array.Empty<TextParagraph>(), VerticalAnchor.Top, img, null, Rotation(xfrm)));
            }
            else if (local == "graphicFrame")
            {
                var xfrm = el.Element(P + "xfrm");
                if (xfrm is null) continue;
                var (x, y, w, h) = Geometry(xfrm, t);
                var tbl = el.Descendants(A + "tbl").FirstOrDefault();
                if (tbl is null) continue;
                var rows = tbl.Elements(A + "tr").Take(20)
                    .Select(tr => (IReadOnlyList<string>)tr.Elements(A + "tc").Take(10)
                        .Select(tc => string.Join(" ", tc.Descendants(A + "t").Select(x => x.Value)))
                        .ToList())
                    .ToList();
                output.Add(new SlideShape(x, y, w, h, ShapeGeometry.Rectangle, null, null, 0, Array.Empty<TextParagraph>(), VerticalAnchor.Top, null, rows));
            }
        }
    }

    private static SlideShape? ParseShape(XElement sp, Transform t, Dictionary<string, (string Type, string Target, bool External)> rels, Context ctx,
        XElement? ph, Dictionary<string, XElement>? layoutPh, Dictionary<string, XElement>? masterPh)
    {
        var spPr = sp.Element(P + "spPr");
        var xfrm = spPr?.Element(A + "xfrm") ?? InheritedXfrm(ph, layoutPh, masterPh);
        if (xfrm is null) return null;
        var (x, y, w, h) = Geometry(xfrm, t);

        var geom = (string?)spPr?.Element(A + "prstGeom")?.Attribute("prst") switch
        {
            "rect" or null => ShapeGeometry.Rectangle,
            "roundRect" or "snip1Rect" or "round1Rect" or "round2SameRect" => ShapeGeometry.RoundRectangle,
            "ellipse" or "circle" or "donut" => ShapeGeometry.Ellipse,
            "line" or "straightConnector1" or "bentConnector3" => ShapeGeometry.Line,
            _ => ShapeGeometry.Other,
        };
        if (sp.Name.LocalName == "cxnSp") geom = ShapeGeometry.Line;

        Fill? fill = ParseFill(spPr, rels, ctx);
        if (fill is null && spPr?.Element(A + "noFill") is null)
        {
            // Style reference: <p:style><a:fillRef idx="1"><a:schemeClr val="accent1"/></a:fillRef>
            var fillRef = sp.Element(P + "style")?.Element(A + "fillRef");
            if (fillRef is not null && (int?)fillRef.Attribute("idx") > 0 && ParseColor(fillRef, ctx) is { } refColor)
                fill = new SolidFill(refColor);
        }

        Argb? lineColor = null;
        double lineWidth = 0;
        var ln = spPr?.Element(A + "ln");
        if (ln is not null && ln.Element(A + "noFill") is null)
        {
            lineColor = ParseColor(ln.Element(A + "solidFill"), ctx);
            lineWidth = (double?)ln.Attribute("w") ?? 12700;
        }
        else if (ln is null)
        {
            var lnRef = sp.Element(P + "style")?.Element(A + "lnRef");
            if (lnRef is not null && (int?)lnRef.Attribute("idx") > 0)
            {
                lineColor = ParseColor(lnRef, ctx);
                lineWidth = 12700;
            }
        }

        var phType = (string?)ph?.Attribute("type");
        var (defSize, defColor) = phType switch
        {
            "title" or "ctrTitle" => ctx.TitleStyle,
            null when ph is null => ctx.OtherStyle,
            _ => ctx.BodyStyle,
        };
        if (phType == "subTitle") defSize = Math.Min(defSize, 24);
        if (phType is "dt" or "ftr" or "sldNum") defSize = 12;
        var fontRefColor = ParseColor(sp.Element(P + "style")?.Element(A + "fontRef"), ctx);

        var txBody = sp.Element(P + "txBody");
        var paragraphs = new List<TextParagraph>();
        var anchor = VerticalAnchor.Top;
        if (txBody is not null)
        {
            anchor = (string?)txBody.Element(A + "bodyPr")?.Attribute("anchor") switch
            {
                "ctr" => VerticalAnchor.Middle,
                "b" => VerticalAnchor.Bottom,
                _ => phType is "title" or "ctrTitle" ? VerticalAnchor.Middle : VerticalAnchor.Top,
            };
            var autofitScale = ((double?)txBody.Element(A + "bodyPr")?.Element(A + "normAutofit")?.Attribute("fontScale") ?? 100000) / 100000.0;
            foreach (var p in txBody.Elements(A + "p").Take(40))
            {
                var pPr = p.Element(A + "pPr");
                var align = (string?)pPr?.Attribute("algn") switch
                {
                    "ctr" => TextAlign.Center,
                    "r" => TextAlign.Right,
                    "just" => TextAlign.Justify,
                    _ => phType == "ctrTitle" || phType == "subTitle" ? TextAlign.Center : TextAlign.Left,
                };
                var level = (int?)pPr?.Attribute("lvl") ?? 0;
                var isContentPlaceholder = ph is not null && phType is null or "body" or "obj";
                var bullet = pPr?.Element(A + "buChar") is not null
                             || pPr?.Element(A + "buAutoNum") is not null
                             || (isContentPlaceholder && pPr?.Element(A + "buNone") is null);
                var runs = new List<TextRun>();
                foreach (var r in p.Elements())
                {
                    if (r.Name.LocalName is not ("r" or "fld")) { if (r.Name.LocalName == "br") runs.Add(new TextRun("\n", defSize)); continue; }
                    var text = r.Element(A + "t")?.Value ?? "";
                    if (text.Length == 0) continue;
                    var rPr = r.Element(A + "rPr");
                    var size = ((double?)rPr?.Attribute("sz") / 100.0 ?? defSize) * autofitScale;
                    var bold = (string?)rPr?.Attribute("b") is "1" or "true";
                    var italic = (string?)rPr?.Attribute("i") is "1" or "true";
                    var underline = (string?)rPr?.Attribute("u") is { } u && u != "none";
                    var color = ParseColor(rPr?.Element(A + "solidFill"), ctx) ?? fontRefColor ?? defColor;
                    var font = (string?)rPr?.Element(A + "latin")?.Attribute("typeface");
                    runs.Add(new TextRun(text, size, bold, italic, underline, color, font is { Length: > 0 } && !font.StartsWith('+') ? font : null));
                }
                paragraphs.Add(new TextParagraph(runs, align, bullet && runs.Count > 0, level));
            }
        }

        // Empty placeholders render nothing in a slide thumbnail.
        if (fill is null && lineColor is null && paragraphs.All(p => p.Runs.Count == 0))
            return null;

        return new SlideShape(x, y, w, h, geom, fill, lineColor, lineWidth, paragraphs, anchor, null, null, Rotation(xfrm));
    }

    private static Dictionary<string, XElement> CollectPlaceholders(XElement? root)
    {
        var map = new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (root is null) return map;
        foreach (var sp in root.Descendants().Where(e => e.Name.LocalName is "sp" or "pic"))
        {
            var ph = sp.Descendants(P + "ph").FirstOrDefault();
            var xfrm = sp.Element(P + "spPr")?.Element(A + "xfrm");
            if (ph is null || xfrm is null) continue;
            var idx = (string?)ph.Attribute("idx");
            var type = (string?)ph.Attribute("type") ?? "body";
            if (idx is not null) map.TryAdd("idx:" + idx, xfrm);
            map.TryAdd("type:" + type, xfrm);
        }
        return map;
    }

    private static XElement? InheritedXfrm(XElement? ph, Dictionary<string, XElement>? layoutPh, Dictionary<string, XElement>? masterPh)
    {
        if (ph is null) return null;
        var idx = (string?)ph.Attribute("idx");
        var type = (string?)ph.Attribute("type") ?? "body";
        var typeKey = "type:" + (type == "ctrTitle" ? "title" : type == "subTitle" ? "body" : type);
        foreach (var map in new[] { layoutPh, masterPh })
        {
            if (map is null) continue;
            if (idx is not null && map.TryGetValue("idx:" + idx, out var byIdx)) return byIdx;
            if (map.TryGetValue("type:" + type, out var byType)) return byType;
            if (map.TryGetValue(typeKey, out var byGeneric)) return byGeneric;
        }
        return null;
    }

    // ------------------------------------------------------------------ fills & colours

    private static Fill? ParseBackground(XElement? root, Dictionary<string, (string Type, string Target, bool External)> rels, Context ctx)
    {
        var bg = root?.Element(P + "cSld")?.Element(P + "bg");
        if (bg is null) return null;
        var bgPr = bg.Element(P + "bgPr");
        if (bgPr is not null) return ParseFill(bgPr, rels, ctx);
        var bgRef = bg.Element(P + "bgRef");
        return ParseColor(bgRef, ctx) is { } c ? new SolidFill(c) : null;
    }

    private static Fill? ParseFill(XElement? container, Dictionary<string, (string Type, string Target, bool External)> rels, Context ctx)
    {
        if (container is null) return null;
        if (container.Element(A + "solidFill") is { } solid && ParseColor(solid, ctx) is { } c)
            return new SolidFill(c);
        if (container.Element(A + "gradFill") is { } grad)
        {
            var stops = grad.Element(A + "gsLst")?.Elements(A + "gs")
                .Select(gs => ((double?)gs.Attribute("pos") / 100000.0 ?? 0, ParseColor(gs, ctx)))
                .Where(s => s.Item2 is not null)
                .Select(s => (s.Item1, s.Item2!.Value))
                .ToList() ?? new();
            var angle = ((double?)grad.Element(A + "lin")?.Attribute("ang") ?? 0) / 60000.0;
            if (stops.Count == 1) return new SolidFill(stops[0].Item2);
            if (stops.Count > 1) return new GradientFill(stops, angle);
        }
        if (container.Element(A + "blipFill") is { } blipFill && ReadBlip(blipFill.Element(A + "blip"), rels, ctx) is { } img)
            return new ImageFill(img);
        return null;
    }

    private static byte[]? ReadBlip(XElement? blip, Dictionary<string, (string Type, string Target, bool External)> rels, Context ctx)
    {
        var embed = (string?)blip?.Attribute(R + "embed");
        if (embed is null || !rels.TryGetValue(embed, out var rel) || rel.External) return null;
        var ext = Path.GetExtension(rel.Target).ToLowerInvariant();
        if (ext is ".emf" or ".wmf" or ".svg") return null; // vector metafiles: not decodable by WIC
        return ctx.Pkg.ReadBytes(rel.Target, SafePackage.MaxImageBytes);
    }

    /// <summary>Reads the first colour child (srgbClr / schemeClr / sysClr / prstClr) of an element.</summary>
    private static Argb? ParseColor(XElement? container, Context ctx)
    {
        if (container is null) return null;
        foreach (var c in container.Elements())
        {
            Argb? baseColor = c.Name.LocalName switch
            {
                "srgbClr" => Argb.TryParseHex((string?)c.Attribute("val")),
                "schemeClr" => ResolveScheme((string?)c.Attribute("val"), ctx),
                "sysClr" => Argb.TryParseHex((string?)c.Attribute("lastClr")) ?? ((string?)c.Attribute("val") == "window" ? Argb.White : Argb.Black),
                "prstClr" => PresetColor((string?)c.Attribute("val")),
                "scrgbClr" => Argb.FromRgb(Pct(c, "r"), Pct(c, "g"), Pct(c, "b")),
                _ => null,
            };
            if (baseColor is null) continue;
            return ApplyModifiers(baseColor.Value, c);
        }
        return null;
    }

    private static byte Pct(XElement c, string attr) => (byte)Math.Clamp(((double?)c.Attribute(attr) ?? 0) / 100000.0 * 255, 0, 255);

    internal static Argb ApplyModifiers(Argb color, XElement colorEl)
    {
        double? Val(string name) => (double?)colorEl.Element(A + name)?.Attribute("val") / 100000.0;
        var lumMod = Val("lumMod");
        var lumOff = Val("lumOff");
        if (lumMod is not null || lumOff is not null)
            color = color.ApplyLuminance(lumMod ?? 1, lumOff ?? 0);
        var tint = Val("tint");
        var shade = Val("shade");
        if (tint is not null || shade is not null)
            color = color.ApplyTintShade(tint, shade);
        if (Val("alpha") is { } alpha)
            color = color.WithAlpha((byte)Math.Clamp(alpha * 255, 0, 255));
        return color;
    }

    private static Argb? ResolveScheme(string? name, Context ctx)
    {
        if (name is null) return null;
        if (ctx.ColorMap.TryGetValue(name, out var mapped)) name = mapped;
        name = name switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => name };
        return ctx.Theme.TryGetValue(name, out var c) ? c : name switch
        {
            "dk1" => Argb.Black,
            "lt1" => Argb.White,
            "dk2" => new Argb(0xFF44546A),
            "lt2" => new Argb(0xFFE7E6E6),
            "accent1" => new Argb(0xFF4472C4),
            "accent2" => new Argb(0xFFED7D31),
            "accent3" => new Argb(0xFFA5A5A5),
            "accent4" => new Argb(0xFFFFC000),
            "accent5" => new Argb(0xFF5B9BD5),
            "accent6" => new Argb(0xFF70AD47),
            _ => null,
        };
    }

    private static Argb? PresetColor(string? v) => v switch
    {
        "black" => Argb.Black,
        "white" => Argb.White,
        "red" => new Argb(0xFFFF0000),
        "green" => new Argb(0xFF008000),
        "blue" => new Argb(0xFF0000FF),
        "yellow" => new Argb(0xFFFFFF00),
        "gray" or "grey" => new Argb(0xFF808080),
        _ => null,
    };

    private static void ReadTheme(XDocument? theme, Context ctx)
    {
        var scheme = theme?.Root?.Descendants(A + "clrScheme").FirstOrDefault();
        if (scheme is null) return;
        foreach (var el in scheme.Elements())
        {
            var c = el.Elements().FirstOrDefault();
            if (c is null) continue;
            var color = c.Name.LocalName switch
            {
                "srgbClr" => Argb.TryParseHex((string?)c.Attribute("val")),
                "sysClr" => Argb.TryParseHex((string?)c.Attribute("lastClr")),
                _ => null,
            };
            if (color is not null) ctx.Theme[el.Name.LocalName] = color.Value;
        }
    }

    private static void ReadColorMap(XDocument? master, Context ctx)
    {
        var map = master?.Root?.Element(P + "clrMap");
        if (map is null) return;
        foreach (var attr in map.Attributes())
            ctx.ColorMap[attr.Name.LocalName] = attr.Value;
    }

    private static void ReadTextStyles(XDocument? master, Context ctx)
    {
        var styles = master?.Root?.Element(P + "txStyles");
        if (styles is null) return;
        (double, Argb?) Read(XElement? style, double fallback)
        {
            var def = style?.Element(A + "lvl1pPr")?.Element(A + "defRPr");
            var size = (double?)def?.Attribute("sz") / 100.0 ?? fallback;
            return (size, ParseColor(def?.Element(A + "solidFill"), ctx));
        }
        ctx.TitleStyle = Read(styles.Element(P + "titleStyle"), 44);
        ctx.BodyStyle = Read(styles.Element(P + "bodyStyle"), 28);
        ctx.OtherStyle = Read(styles.Element(P + "otherStyle"), 18);
    }

    // ------------------------------------------------------------------ geometry helpers

    private static (double X, double Y, double W, double H) Geometry(XElement xfrm, Transform t)
    {
        var (x, y) = Point(xfrm.Element(A + "off"));
        var (w, h) = Size(xfrm.Element(A + "ext"));
        return t.Apply(x, y, w, h);
    }

    private static double Rotation(XElement xfrm) => ((double?)xfrm.Attribute("rot") ?? 0) / 60000.0;

    private static (double, double) Point(XElement? el)
        => (Num((string?)el?.Attribute("x")), Num((string?)el?.Attribute("y")));

    private static (double, double) Size(XElement? el)
        => (Num((string?)el?.Attribute("cx")), Num((string?)el?.Attribute("cy")));

    private static double Num(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
