using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace DownloadDock.Core.Previews;

/// <summary>
/// Defensive read-only access to ZIP-based documents (OOXML / ODF). Guards against zip
/// bombs (entry size caps), XXE (DTDs prohibited, no resolver) and path tricks. Nothing in
/// the document is ever executed – macros, OLE objects and external links are ignored.
/// </summary>
public sealed class SafePackage : IDisposable
{
    public const long MaxXmlBytes = 32L * 1024 * 1024;
    public const long MaxImageBytes = 24L * 1024 * 1024;
    public const long MaxTotalBytesRead = 96L * 1024 * 1024;

    private readonly ZipArchive _zip;
    private readonly Stream _stream;
    private long _totalRead;

    private SafePackage(Stream stream)
    {
        _stream = stream;
        _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    /// <summary>Opens the file for reading with sharing so a file open in another app can still be previewed.</summary>
    public static SafePackage? TryOpen(string path)
    {
        Stream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            return new SafePackage(fs);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            fs?.Dispose();
            return null;
        }
    }

    public static SafePackage? TryOpen(Stream stream)
    {
        try { return new SafePackage(stream); }
        catch (InvalidDataException) { return null; }
    }

    public bool Exists(string partName) => FindEntry(partName) is not null;

    private ZipArchiveEntry? FindEntry(string partName)
    {
        var name = NormalizePartName(partName);
        return _zip.GetEntry(name) ?? _zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
    }

    public byte[]? ReadBytes(string partName, long maxBytes)
    {
        var entry = FindEntry(partName);
        if (entry is null || entry.Length > maxBytes || entry.Length < 0)
            return null;
        if (_totalRead + entry.Length > MaxTotalBytesRead)
            return null;

        using var s = entry.Open();
        var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        var chunk = new byte[81920];
        long read = 0;
        int n;
        while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            read += n;
            if (read > maxBytes) return null; // header lied about the size
            buffer.Write(chunk, 0, n);
        }
        _totalRead += read;
        return buffer.ToArray();
    }

    public XDocument? ReadXml(string partName)
    {
        var bytes = ReadBytes(partName, MaxXmlBytes);
        if (bytes is null) return null;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxXmlBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var ms = new MemoryStream(bytes);
            using var reader = XmlReader.Create(ms, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Reads the relationships of a part: Id → (Type, absolute target part name).</summary>
    public Dictionary<string, (string Type, string Target, bool External)> ReadRelationships(string partName)
    {
        var result = new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal);
        var normalized = NormalizePartName(partName);
        var dir = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
        var file = normalized.Contains('/') ? normalized[(normalized.LastIndexOf('/') + 1)..] : normalized;
        var relsPath = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + file + ".rels";
        var doc = ReadXml(relsPath);
        if (doc?.Root is null) return result;
        foreach (var rel in doc.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            var type = (string?)rel.Attribute("Type") ?? "";
            var external = string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
            if (id is null || target is null) continue;
            result[id] = (type, external ? target : ResolvePath(dir, target), external);
        }
        return result;
    }

    /// <summary>Resolves a relative OPC target against a base directory, refusing to escape the package root.</summary>
    public static string ResolvePath(string baseDir, string target)
    {
        target = Uri.UnescapeDataString(target.Replace('\\', '/'));
        var parts = new List<string>();
        if (!target.StartsWith('/') && baseDir.Length > 0)
            parts.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static string NormalizePartName(string partName) => partName.TrimStart('/').Replace('\\', '/');

    public IEnumerable<string> EntryNames => _zip.Entries.Select(e => e.FullName);

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}
