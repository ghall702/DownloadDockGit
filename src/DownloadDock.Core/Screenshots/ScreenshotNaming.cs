using System.Globalization;

namespace DownloadDock.Core.Screenshots;

/// <summary>macOS-style screenshot file names: "Screenshot 2026-11-09 08-30-15.png", never overwriting.</summary>
public static class ScreenshotNaming
{
    public static string BaseName(DateTime localTime)
        => "Screenshot " + localTime.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>Returns a path in <paramref name="folder"/> that doesn't exist yet ("… (2).png", "… (3).png", …).</summary>
    public static string UniquePath(string folder, DateTime localTime, Func<string, bool>? exists = null, string extension = ".png")
    {
        exists ??= File.Exists;
        var name = BaseName(localTime);
        var candidate = Path.Combine(folder, name + extension);
        for (var i = 2; exists(candidate); i++)
            candidate = Path.Combine(folder, $"{name} ({i}){extension}");
        return candidate;
    }

    public static bool IsImageFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
}
