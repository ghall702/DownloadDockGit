using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadDock.Core.Previews;

namespace DownloadDock.Thumbnails;

/// <summary>
/// Draws previews of Office and text documents from the models produced by the safe parsers in
/// DownloadDock.Core. Used when no Shell thumbnail provider (e.g. Microsoft Office) is installed and
/// the file carries no embedded thumbnail. Must run on an STA thread; results are frozen.
/// </summary>
internal static class DocumentPreviewRenderer
{
    private static readonly FontFamily DocumentFont = new("Aptos, Calibri, Segoe UI");
    private static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");
    private const double EmuPerPoint = 12700;

    // ------------------------------------------------------------------ Word

    public static BitmapSource RenderDocument(DocumentModel doc, int maxPixels)
    {
        var pageW = Math.Max(100, doc.PageWidthPt);
        var pageH = Math.Max(100, doc.PageHeightPt);
        var scale = maxPixels / Math.Max(pageW, pageH);
        var pxW = Math.Max(1, (int)Math.Round(pageW * scale));
        var pxH = Math.Max(1, (int)Math.Round(pageH * scale));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageW, pageH));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, pageW, pageH)));

            var left = doc.MarginLeftPt;
            var contentW = Math.Max(50, pageW - doc.MarginLeftPt - doc.MarginRightPt);
            var bottom = pageH - doc.MarginBottomPt;
            var y = doc.MarginTopPt;

            foreach (var block in doc.Blocks)
            {
                if (y >= bottom) break;
                switch (block)
                {
                    case DocParagraph p:
                    {
                        var before = p.Role switch
                        {
                            ParagraphRole.Heading1 => 12,
                            ParagraphRole.Heading2 or ParagraphRole.Heading3 => 6,
                            _ => 0,
                        };
                        y += before;
                        var indent = p.Paragraph.Bullet ? 18 + 18 * p.Paragraph.Level : 0;
                        var ft = BuildText(p.Paragraph, contentW - indent, Brushes.Black, DocumentFont, bottom - y);
                        if (ft is null)
                        {
                            y += (p.Paragraph.Runs.FirstOrDefault()?.SizePt ?? 11) * 1.15 + p.Paragraph.SpaceAfterPt;
                            break;
                        }
                        if (p.Paragraph.Bullet)
                        {
                            var bullet = MakeText("•", ft.Height > 0 ? p.Paragraph.Runs[0].SizePt : 11, Brushes.Black, DocumentFont);
                            dc.DrawText(bullet, new Point(left + indent - 12, y));
                        }
                        dc.DrawText(ft, new Point(left + indent, y));
                        y += ft.Height + p.Paragraph.SpaceAfterPt;
                        break;
                    }
                    case DocImage img:
                    {
                        var bmp = DecodeImage(img.Data, (int)Math.Ceiling(contentW * scale * 2));
                        if (bmp is null) break;
                        var w = img.WidthPt > 0 ? img.WidthPt : bmp.Width;
                        var h = img.HeightPt > 0 ? img.HeightPt : bmp.Height;
                        if (w > contentW) { h *= contentW / w; w = contentW; }
                        dc.DrawImage(bmp, new Rect(left + (contentW - w) / 2, y, w, h));
                        y += h + 8;
                        break;
                    }
                    case DocTable table:
                    {
                        y = DrawTable(dc, table.Rows, left, y, contentW, 9, bottom) + 8;
                        break;
                    }
                }
            }
            dc.Pop();
            dc.Pop();
        }
        return ToBitmap(visual, pxW, pxH);
    }

    private static double DrawTable(DrawingContext dc, IReadOnlyList<IReadOnlyList<string>> rows, double x, double y, double width, double fontPt, double bottom)
    {
        var cols = Math.Max(1, rows.Max(r => r.Count));
        var colW = width / cols;
        var rowH = fontPt * 1.9;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xBF, 0xBF, 0xBF)), 0.6);
        pen.Freeze();
        for (var r = 0; r < rows.Count && y + rowH <= bottom; r++)
        {
            if (r == 0)
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), null, new Rect(x, y, width, rowH));
            for (var c = 0; c < cols; c++)
            {
                var cell = new Rect(x + c * colW, y, colW, rowH);
                dc.DrawRectangle(null, pen, cell);
                if (c < rows[r].Count && rows[r][c].Length > 0)
                {
                    var ft = MakeText(rows[r][c], fontPt, Brushes.Black, DocumentFont, r == 0);
                    ft.MaxTextWidth = Math.Max(1, colW - 6);
                    ft.MaxLineCount = 1;
                    ft.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(ft, new Point(cell.X + 3, cell.Y + (rowH - ft.Height) / 2));
                }
            }
            y += rowH;
        }
        return y;
    }

    // ------------------------------------------------------------------ PowerPoint

    public static BitmapSource RenderSlide(SlideModel slide, int maxPixels)
    {
        var wPt = Math.Max(1, slide.WidthEmu / EmuPerPoint);
        var hPt = Math.Max(1, slide.HeightEmu / EmuPerPoint);
        var scale = maxPixels / Math.Max(wPt, hPt);
        var pxW = Math.Max(1, (int)Math.Round(wPt * scale));
        var pxH = Math.Max(1, (int)Math.Round(hPt * scale));
        var slideRect = new Rect(0, 0, wPt, hPt);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushClip(new RectangleGeometry(slideRect));
            var bgBrush = ToBrush(slide.Background, slideRect.Size, pxW) ?? Brushes.White;
            dc.DrawRectangle(bgBrush, null, slideRect);
            var bgIsDark = EstimateLuminance(slide.Background) < 0.45;

            foreach (var s in slide.Shapes)
            {
                var r = new Rect(s.X / EmuPerPoint, s.Y / EmuPerPoint, Math.Max(0, s.Width / EmuPerPoint), Math.Max(0, s.Height / EmuPerPoint));
                if (r.Width <= 0 && r.Height <= 0) continue;
                var rotated = Math.Abs(s.RotationDegrees) > 0.01;
                if (rotated) dc.PushTransform(new RotateTransform(s.RotationDegrees, r.X + r.Width / 2, r.Y + r.Height / 2));

                if (s.Image is not null)
                {
                    var bmp = DecodeImage(s.Image, (int)Math.Ceiling(Math.Max(r.Width, r.Height) * scale * 2));
                    if (bmp is not null) dc.DrawImage(bmp, r);
                }
                else
                {
                    var fill = ToBrush(s.Fill, r.Size, (int)(r.Width * scale));
                    Pen? pen = null;
                    if (s.LineColor is { } lc)
                    {
                        pen = new Pen(new SolidColorBrush(ToColor(lc)), Math.Max(0.5, s.LineWidthEmu / EmuPerPoint));
                        pen.Freeze();
                    }
                    if (fill is not null || pen is not null)
                    {
                        switch (s.Geometry)
                        {
                            case ShapeGeometry.Ellipse:
                                dc.DrawEllipse(fill, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                                break;
                            case ShapeGeometry.RoundRectangle:
                                var radius = Math.Min(r.Width, r.Height) * 0.16;
                                dc.DrawRoundedRectangle(fill, pen, r, radius, radius);
                                break;
                            case ShapeGeometry.Line:
                                if (pen is not null) dc.DrawLine(pen, r.TopLeft, r.BottomRight);
                                break;
                            default:
                                dc.DrawRectangle(fill, pen, r);
                                break;
                        }
                    }

                    if (s.Table is { Count: > 0 } table)
                        DrawTable(dc, table, r.X, r.Y, r.Width, 10, slideRect.Bottom);

                    if (s.Paragraphs.Count > 0)
                    {
                        var shapeDark = s.Fill is not null ? EstimateLuminance(s.Fill) < 0.45 : bgIsDark;
                        var defaultBrush = shapeDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                        DrawShapeText(dc, s, r, defaultBrush);
                    }
                }
                if (rotated) dc.Pop();
            }
            dc.Pop();
            dc.Pop();
        }
        return ToBitmap(visual, pxW, pxH);
    }

    private static void DrawShapeText(DrawingContext dc, SlideShape s, Rect r, Brush defaultBrush)
    {
        const double insetX = 7.2, insetY = 3.6;
        var width = Math.Max(1, r.Width - 2 * insetX);
        var texts = new List<(FormattedText Text, double Indent, bool Bullet, double Size)>();
        double total = 0;
        foreach (var p in s.Paragraphs)
        {
            var indent = p.Bullet ? 20 + 24 * p.Level : 24 * p.Level;
            var ft = BuildText(p, Math.Max(1, width - indent), defaultBrush, DocumentFont, double.PositiveInfinity);
            var size = p.Runs.Count > 0 ? p.Runs[0].SizePt : 18;
            if (ft is null)
            {
                total += size * 1.2;
                texts.Add((MakeText(" ", size, defaultBrush, DocumentFont), indent, false, size));
                continue;
            }
            texts.Add((ft, indent, p.Bullet, size));
            total += ft.Height + size * 0.2;
        }

        var y = s.Anchor switch
        {
            VerticalAnchor.Middle => r.Y + (r.Height - total) / 2,
            VerticalAnchor.Bottom => r.Bottom - insetY - total,
            _ => r.Y + insetY,
        };
        foreach (var (text, indent, bullet, size) in texts)
        {
            if (bullet)
                dc.DrawText(MakeText("•", size, defaultBrush, DocumentFont), new Point(r.X + insetX + indent - 16, y));
            dc.DrawText(text, new Point(r.X + insetX + indent, y));
            y += text.Height + size * 0.2;
        }
    }

    // ------------------------------------------------------------------ Excel

    public static BitmapSource RenderSheet(SheetModel sheet, int maxPixels)
    {
        const double canvasW = 640, canvasH = 480;
        const double headerH = 20, rowHeaderW = 34, rowH = 20, tabBarH = 24;
        var scale = maxPixels / canvasW;
        var pxW = maxPixels;
        var pxH = Math.Max(1, (int)Math.Round(canvasH * scale));

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1)), 1); gridPen.Freeze();
        var headerPen = new Pen(new SolidColorBrush(Color.FromRgb(0xC6, 0xC6, 0xC6)), 1); headerPen.Freeze();
        var headerBg = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)); headerBg.Freeze();
        var headerFg = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)); headerFg.Freeze();
        var cellFg = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)); cellFg.Freeze();
        var excelGreen = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41)); excelGreen.Freeze();

        // Column geometry.
        var colX = new List<double> { rowHeaderW };
        for (var c = 0; colX[^1] < canvasW; c++)
        {
            var chars = sheet.ColumnWidthChars.TryGetValue(c, out var wc) ? wc : 8.43;
            colX.Add(colX[^1] + Math.Max(chars > 0 ? 18 : 0, chars * 7 + 5));
            if (c > 60) break;
        }
        var visibleCols = colX.Count - 1;
        var visibleRows = (int)((canvasH - headerH - tabBarH) / rowH) + 1;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, canvasW, canvasH));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, canvasW, canvasH - tabBarH)));

            // Cell fills first, then grid, then text.
            for (var r = 0; r < visibleRows; r++)
            {
                for (var c = 0; c < visibleCols; c++)
                {
                    if (sheet.Cells.TryGetValue((r, c), out var cell) && cell.Fill is { } f)
                        dc.DrawRectangle(new SolidColorBrush(ToColor(f)), null, new Rect(colX[c], headerH + r * rowH, colX[c + 1] - colX[c], rowH));
                }
            }
            if (sheet.ShowGridLines)
            {
                for (var c = 0; c <= visibleCols; c++) dc.DrawLine(gridPen, new Point(colX[c] + 0.5, headerH), new Point(colX[c] + 0.5, canvasH));
                for (var r = 0; r <= visibleRows; r++) dc.DrawLine(gridPen, new Point(rowHeaderW, headerH + r * rowH + 0.5), new Point(canvasW, headerH + r * rowH + 0.5));
            }

            for (var r = 0; r < visibleRows; r++)
            {
                for (var c = 0; c < visibleCols; c++)
                {
                    if (!sheet.Cells.TryGetValue((r, c), out var cell) || cell.Text.Length == 0) continue;
                    var brush = cell.FontColor is { } fc ? new SolidColorBrush(ToColor(fc)) : cellFg;
                    var ft = MakeText(cell.Text, 11, brush, DocumentFont, cell.Bold);
                    ft.MaxLineCount = 1;
                    var x0 = colX[c];
                    var cellW = colX[c + 1] - x0;
                    // Text overflows into empty neighbours, like Excel.
                    var span = cellW;
                    if (!cell.IsNumber)
                    {
                        for (var n = c + 1; n < visibleCols && !sheet.Cells.ContainsKey((r, n)); n++)
                            span += colX[n + 1] - colX[n];
                    }
                    ft.MaxTextWidth = Math.Max(1, (cell.IsNumber ? cellW : span) - 6);
                    ft.Trimming = cell.IsNumber ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                    if (cell.IsNumber) ft.TextAlignment = TextAlignment.Right;
                    dc.DrawText(ft, new Point(x0 + 3, headerH + r * rowH + (rowH - ft.Height) / 2));
                }
            }

            // Headers.
            dc.DrawRectangle(headerBg, null, new Rect(0, 0, canvasW, headerH));
            dc.DrawRectangle(headerBg, null, new Rect(0, 0, rowHeaderW, canvasH));
            for (var c = 0; c < visibleCols; c++)
            {
                dc.DrawLine(headerPen, new Point(colX[c + 1] + 0.5, 0), new Point(colX[c + 1] + 0.5, headerH));
                var ft = MakeText(ColumnName(c), 11, headerFg, UiFont);
                dc.DrawText(ft, new Point((colX[c] + colX[c + 1] - ft.Width) / 2, (headerH - ft.Height) / 2));
            }
            for (var r = 0; r < visibleRows; r++)
            {
                dc.DrawLine(headerPen, new Point(0, headerH + (r + 1) * rowH + 0.5), new Point(rowHeaderW, headerH + (r + 1) * rowH + 0.5));
                var ft = MakeText((r + 1).ToString(CultureInfo.InvariantCulture), 11, headerFg, UiFont);
                dc.DrawText(ft, new Point((rowHeaderW - ft.Width) / 2, headerH + r * rowH + (rowH - ft.Height) / 2));
            }
            dc.DrawLine(headerPen, new Point(0, headerH + 0.5), new Point(canvasW, headerH + 0.5));
            dc.DrawLine(headerPen, new Point(rowHeaderW + 0.5, 0), new Point(rowHeaderW + 0.5, canvasH));
            dc.Pop();

            // Sheet tab bar.
            var tabTop = canvasH - tabBarH;
            dc.DrawRectangle(headerBg, null, new Rect(0, tabTop, canvasW, tabBarH));
            var name = MakeText(sheet.SheetName, 11, excelGreen, UiFont, bold: true);
            var tab = new Rect(rowHeaderW, tabTop, Math.Min(canvasW - rowHeaderW, name.Width + 24), tabBarH);
            dc.DrawRectangle(Brushes.White, null, tab);
            dc.DrawRectangle(excelGreen, null, new Rect(tab.X, tab.Bottom - 3, tab.Width, 3));
            dc.DrawText(name, new Point(tab.X + 12, tabTop + (tabBarH - name.Height) / 2 - 1));
            dc.Pop();
        }
        return ToBitmap(visual, pxW, pxH);
    }

    internal static string ColumnName(int index)
    {
        var name = "";
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            name = (char)('A' + rem) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    // ------------------------------------------------------------------ Plain text

    public static BitmapSource RenderText(IReadOnlyList<string> lines, string extension, int maxPixels)
    {
        const double pageW = 600, pageH = 776, margin = 34;
        var ext = extension.ToLowerInvariant();
        var isMarkdown = ext is ".md" or ".markdown";
        var prose = isMarkdown || ext == ".txt";
        var font = prose ? DocumentFont : MonoFont;
        var size = prose ? 15 : 13;
        var scale = maxPixels / pageH;
        var pxW = Math.Max(1, (int)Math.Round(pageW * scale));
        var pxH = maxPixels;
        var fg = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)); fg.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageW, pageH));
            var y = margin;
            foreach (var raw in lines)
            {
                var line = raw;
                var lineSize = size;
                var bold = false;
                if (isMarkdown && line.StartsWith('#'))
                {
                    var level = line.TakeWhile(ch => ch == '#').Count();
                    line = line.TrimStart('#').TrimStart();
                    lineSize = Math.Max(size, 28 - level * 4);
                    bold = true;
                }
                var ft = MakeText(line.Length == 0 ? " " : line, lineSize, fg, font, bold);
                ft.MaxTextWidth = pageW - 2 * margin;
                ft.MaxLineCount = prose ? 4 : 1;
                ft.Trimming = TextTrimming.None;
                if (y + ft.Height > pageH - margin) break;
                dc.DrawText(ft, new Point(margin, y));
                y += ft.Height + (prose ? 4 : 1);
            }
            dc.Pop();
        }
        return ToBitmap(visual, pxW, pxH);
    }

    // ------------------------------------------------------------------ helpers

    private static FormattedText? BuildText(TextParagraph p, double maxWidth, Brush defaultBrush, FontFamily defaultFont, double maxHeight)
    {
        var text = p.PlainText;
        if (text.Length == 0) return null;
        var first = p.Runs[0];
        var ft = MakeText(text, Math.Max(1, first.SizePt), defaultBrush, defaultFont);
        ft.MaxTextWidth = Math.Max(1, maxWidth);
        if (!double.IsInfinity(maxHeight) && maxHeight > 0) ft.MaxTextHeight = maxHeight;
        ft.TextAlignment = p.Align switch
        {
            TextAlign.Center => TextAlignment.Center,
            TextAlign.Right => TextAlignment.Right,
            TextAlign.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
        var index = 0;
        foreach (var run in p.Runs)
        {
            var len = run.Text.Length;
            if (len == 0) continue;
            ft.SetFontSize(Math.Max(1, run.SizePt), index, len);
            if (run.Bold) ft.SetFontWeight(FontWeights.Bold, index, len);
            if (run.Italic) ft.SetFontStyle(FontStyles.Italic, index, len);
            if (run.Underline) ft.SetTextDecorations(TextDecorations.Underline, index, len);
            if (run.Color is { } c) ft.SetForegroundBrush(new SolidColorBrush(ToColor(c)), index, len);
            if (run.Font is { Length: > 0 } font) ft.SetFontFamily(new FontFamily(font + ", " + defaultFont.Source), index, len);
            index += len;
        }
        return ft;
    }

    private static FormattedText MakeText(string text, double size, Brush brush, FontFamily font, bool bold = false)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            Math.Clamp(size, 0.5, 2000), brush, 1.0);

    private static Brush? ToBrush(Fill? fill, Size size, int pixelWidth)
    {
        switch (fill)
        {
            case SolidFill s:
                return new SolidColorBrush(ToColor(s.Color));
            case GradientFill g:
            {
                var stops = new GradientStopCollection(g.Stops.Select(st => new GradientStop(ToColor(st.Color), st.Position)));
                // DrawingML angle: 0° = left→right, clockwise.
                var rad = g.AngleDegrees * Math.PI / 180;
                var dx = Math.Cos(rad) / 2;
                var dy = Math.Sin(rad) / 2;
                return new LinearGradientBrush(stops, new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
            }
            case ImageFill i:
            {
                var bmp = DecodeImage(i.Data, Math.Max(64, pixelWidth * 2));
                return bmp is null ? null : new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            default:
                return null;
        }
    }

    private static double EstimateLuminance(Fill? fill) => fill switch
    {
        SolidFill s => s.Color.Luminance,
        GradientFill g when g.Stops.Count > 0 => g.Stops.Average(st => st.Color.Luminance),
        ImageFill => 0.5,
        _ => 1.0,
    };

    private static Color ToColor(Argb c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static BitmapSource? DecodeImage(byte[] data, int maxPixels)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            return ImageThumbnailer.Render(ms, Math.Clamp(maxPixels, 16, 2048));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }

    private static BitmapSource ToBitmap(Visual visual, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
