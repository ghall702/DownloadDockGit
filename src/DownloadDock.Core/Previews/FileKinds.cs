namespace DownloadDock.Core.Previews;

public enum PreviewKind
{
    Image,
    Pdf,
    WordDocument,
    Presentation,
    Spreadsheet,
    OpenDocument,
    Text,
    Video,
    Other,
}

/// <summary>Maps file extensions to the preview pipeline used for them.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, PreviewKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images decoded through WIC (WebP/HEIC/AVIF need the matching Windows codec; Shell is the fallback).
        [".jpg"] = PreviewKind.Image, [".jpeg"] = PreviewKind.Image, [".jpe"] = PreviewKind.Image, [".jfif"] = PreviewKind.Image,
        [".png"] = PreviewKind.Image, [".gif"] = PreviewKind.Image, [".bmp"] = PreviewKind.Image, [".dib"] = PreviewKind.Image,
        [".tif"] = PreviewKind.Image, [".tiff"] = PreviewKind.Image, [".ico"] = PreviewKind.Image, [".webp"] = PreviewKind.Image,
        [".heic"] = PreviewKind.Image, [".heif"] = PreviewKind.Image, [".avif"] = PreviewKind.Image, [".jxr"] = PreviewKind.Image,
        [".wdp"] = PreviewKind.Image, [".dds"] = PreviewKind.Image,

        [".pdf"] = PreviewKind.Pdf,

        [".docx"] = PreviewKind.WordDocument, [".docm"] = PreviewKind.WordDocument,
        [".dotx"] = PreviewKind.WordDocument, [".dotm"] = PreviewKind.WordDocument,

        [".pptx"] = PreviewKind.Presentation, [".pptm"] = PreviewKind.Presentation,
        [".ppsx"] = PreviewKind.Presentation, [".ppsm"] = PreviewKind.Presentation,
        [".potx"] = PreviewKind.Presentation, [".potm"] = PreviewKind.Presentation,

        [".xlsx"] = PreviewKind.Spreadsheet, [".xlsm"] = PreviewKind.Spreadsheet,
        [".xltx"] = PreviewKind.Spreadsheet, [".xltm"] = PreviewKind.Spreadsheet,

        [".odt"] = PreviewKind.OpenDocument, [".ods"] = PreviewKind.OpenDocument, [".odp"] = PreviewKind.OpenDocument,
        [".odg"] = PreviewKind.OpenDocument, [".ott"] = PreviewKind.OpenDocument, [".ots"] = PreviewKind.OpenDocument,
        [".otp"] = PreviewKind.OpenDocument,

        [".txt"] = PreviewKind.Text, [".md"] = PreviewKind.Text, [".markdown"] = PreviewKind.Text, [".log"] = PreviewKind.Text,
        [".csv"] = PreviewKind.Text, [".tsv"] = PreviewKind.Text, [".json"] = PreviewKind.Text, [".xml"] = PreviewKind.Text,
        [".yaml"] = PreviewKind.Text, [".yml"] = PreviewKind.Text, [".ini"] = PreviewKind.Text, [".cfg"] = PreviewKind.Text,
        [".conf"] = PreviewKind.Text, [".toml"] = PreviewKind.Text, [".cs"] = PreviewKind.Text, [".js"] = PreviewKind.Text,
        [".ts"] = PreviewKind.Text, [".py"] = PreviewKind.Text, [".java"] = PreviewKind.Text, [".c"] = PreviewKind.Text,
        [".cpp"] = PreviewKind.Text, [".h"] = PreviewKind.Text, [".css"] = PreviewKind.Text, [".sql"] = PreviewKind.Text,
        [".ps1"] = PreviewKind.Text, [".sh"] = PreviewKind.Text, [".bat"] = PreviewKind.Text, [".rtf"] = PreviewKind.Other,
        [".srt"] = PreviewKind.Text, [".vtt"] = PreviewKind.Text,

        [".mp4"] = PreviewKind.Video, [".mov"] = PreviewKind.Video, [".mkv"] = PreviewKind.Video, [".avi"] = PreviewKind.Video,
        [".wmv"] = PreviewKind.Video, [".webm"] = PreviewKind.Video, [".m4v"] = PreviewKind.Video,
    };

    public static PreviewKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && Map.TryGetValue(ext, out var kind) ? kind : PreviewKind.Other;
    }

    /// <summary>True for formats whose preview is expensive enough to be worth caching on disk.</summary>
    public static bool IsWorthDiskCaching(PreviewKind kind) => kind is not PreviewKind.Image;
}
