using System.Text;

namespace DownloadDock.Core.Previews;

/// <summary>Reads the beginning of a plain-text file for a "page of text" preview.</summary>
public static class TextPreviewReader
{
    public const int MaxBytes = 16 * 1024;

    /// <summary>Returns up to <paramref name="maxLines"/> lines, or null if the file looks binary.</summary>
    public static IReadOnlyList<string>? TryRead(string path, int maxLines = 48, int maxColumns = 120)
    {
        byte[] buffer;
        int read;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            buffer = new byte[MaxBytes];
            read = 0;
            int n;
            while (read < buffer.Length && (n = fs.Read(buffer, read, buffer.Length - read)) > 0)
                read += n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Decode(buffer.AsSpan(0, read), maxLines, maxColumns);
    }

    internal static IReadOnlyList<string>? Decode(ReadOnlySpan<byte> data, int maxLines, int maxColumns)
    {
        Encoding encoding;
        var offset = 0;
        if (data.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = Encoding.UTF8; offset = 3; }
        else if (data.StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = Encoding.Unicode; offset = 2; }
        else if (data.StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = Encoding.BigEndianUnicode; offset = 2; }
        else
        {
            // NUL bytes without a UTF-16 BOM => binary.
            if (data.IndexOf((byte)0) >= 0) return null;
            encoding = new UTF8Encoding(false, throwOnInvalidBytes: false);
        }

        var text = encoding.GetString(data[offset..]);
        var control = 0;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t' or '\f')) control++;
        }
        if (text.Length > 0 && control > text.Length / 20) return null;

        var lines = new List<string>(maxLines);
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Replace("\t", "    ");
            if (line.Length > maxColumns) line = line[..maxColumns];
            lines.Add(line);
            if (lines.Count >= maxLines) break;
        }
        return lines;
    }
}
