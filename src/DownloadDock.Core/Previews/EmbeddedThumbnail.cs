namespace DownloadDock.Core.Previews;

/// <summary>
/// Pulls the thumbnail image that Office (docProps/thumbnail.*) and LibreOffice/ODF
/// (Thumbnails/thumbnail.png) store inside the document package when it is saved.
/// </summary>
public static class EmbeddedThumbnail
{
    private const string ThumbnailRelType = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail";

    public static byte[]? TryExtract(string path)
    {
        using var pkg = SafePackage.TryOpen(path);
        return pkg is null ? null : TryExtract(pkg);
    }

    public static byte[]? TryExtract(SafePackage pkg)
    {
        // ODF
        if (pkg.Exists("Thumbnails/thumbnail.png"))
            return pkg.ReadBytes("Thumbnails/thumbnail.png", SafePackage.MaxImageBytes);

        // OOXML: follow the package-level thumbnail relationship.
        foreach (var rel in pkg.ReadRelationships("").Values)
        {
            if (rel.External || !string.Equals(rel.Type, ThumbnailRelType, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsRasterImage(rel.Target)) continue; // .emf/.wmf thumbnails aren't decodable by WIC
            return pkg.ReadBytes(rel.Target, SafePackage.MaxImageBytes);
        }

        foreach (var candidate in new[] { "docProps/thumbnail.jpeg", "docProps/thumbnail.jpg", "docProps/thumbnail.png" })
        {
            if (pkg.Exists(candidate))
                return pkg.ReadBytes(candidate, SafePackage.MaxImageBytes);
        }
        return null;
    }

    private static bool IsRasterImage(string partName)
        => Path.GetExtension(partName).ToLowerInvariant() is ".jpeg" or ".jpg" or ".png" or ".gif" or ".bmp";
}
