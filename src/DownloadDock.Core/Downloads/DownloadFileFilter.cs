namespace DownloadDock.Core.Downloads;

/// <summary>
/// Decides whether a file in the Downloads folder is a finished download that
/// should appear in the fan, or a temporary / partial / system file that must be hidden.
/// </summary>
public static class DownloadFileFilter
{
    /// <summary>Extensions used by browsers and download managers for in-progress downloads.</summary>
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload",   // Chrome, Edge, Brave, Vivaldi, Opera (Chromium)
        ".part",         // Firefox
        ".partial",      // Internet Explorer / legacy Edge
        ".download",     // Safari
        ".opdownload",   // Opera (Presto)
        ".tmp",
        ".temp",
        ".!ut",          // uTorrent
        ".!qb",          // qBittorrent
        ".bc!",          // BitComet
        ".aria2",        // aria2 control file
        ".td",           // Thunder
        ".dtapart",      // DownThemAll
        ".fdmdownload",  // Free Download Manager
        ".xltd",         // Xunlei
        ".idm",          // Internet Download Manager temp
        ".jdownloader",
        ".filepart",     // WinSCP
        ".adadownload",
    };

    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "thumbs.db",
        "ehthumbs.db",
        ".ds_store",
        "folder.jpg",
    };

    /// <summary>Returns true when the file name looks like an in-progress or temporary download.</summary>
    public static bool IsTemporaryOrPartial(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        var name = Path.GetFileName(fileName);
        if (name.Length == 0)
            return true;

        if (IgnoredFileNames.Contains(name))
            return true;

        // Office lock files ("~$Report.docx") and generic "~" temp files ("~WRL0001.tmp").
        if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith("~", StringComparison.Ordinal))
            return true;

        // Chrome writes "Unconfirmed 123456.crdownload" first; covered by extension, but older
        // builds used ".crdownload" only after rename, so check the prefix too.
        if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
            return true;

        // Edge sometimes uses ".partial" with extra numeric suffixes, Firefox "file.pdf.part".
        var ext = Path.GetExtension(name);
        if (ext.Length > 0 && PartialExtensions.Contains(ext))
            return true;

        // Chrome may append a numbered temp suffix: "file.pdf.crdownload (1)" – very rare but cheap to check.
        foreach (var partial in PartialExtensions)
        {
            if (name.Contains(partial + " (", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Returns true when the file attributes indicate a file the user should not see.</summary>
    public static bool HasHiddenAttributes(FileAttributes attributes)
        => (attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary | FileAttributes.Directory)) != 0;

    /// <summary>Full check combining name and attributes.</summary>
    public static bool ShouldShow(string fileName, FileAttributes attributes)
        => !HasHiddenAttributes(attributes) && !IsTemporaryOrPartial(fileName);
}
