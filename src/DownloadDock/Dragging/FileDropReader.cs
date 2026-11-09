using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace DownloadDock.Dragging;

/// <summary>
/// Extracts real file-system paths from a dropped data object. Reads CF_HDROP (FileDrop) – what Explorer,
/// browsers and DownloadDock itself provide – and falls back to the Shell's "Shell IDList Array" (CIDA),
/// which some file managers (including shell-item based ones such as Finder for Windows may be) put on a
/// drag without CF_HDROP. Virtual items with no file-system path (e.g. Control Panel) are skipped.
/// </summary>
internal static class FileDropReader
{
    public const string ShellIdListFormat = "Shell IDList Array";

    public static bool HasFiles(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(ShellIdListFormat);

    public static string[]? GetFiles(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                return files;
            if (data.GetDataPresent(ShellIdListFormat) && data.GetData(ShellIdListFormat) is MemoryStream cida)
            {
                var paths = PathsFromCida(cida.ToArray());
                return paths.Count > 0 ? paths.ToArray() : null;
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            Services.Log.Error("Reading dropped files failed", ex);
        }
        return null;
    }

    /// <summary>Decodes a CIDA blob (UINT cidl; UINT offsets[cidl+1]; parent PIDL; child PIDLs…) into paths.</summary>
    internal static List<string> PathsFromCida(byte[] blob)
    {
        var result = new List<string>();
        if (blob.Length < 8) return result;
        var count = BitConverter.ToUInt32(blob, 0);
        if (count == 0 || count > 10_000 || 4 + 4 * (long)(count + 1) > blob.Length) return result;

        var buffer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, buffer, blob.Length);
            var parentOffset = BitConverter.ToUInt32(blob, 4);
            if (parentOffset >= blob.Length) return result;
            var parent = buffer + (int)parentOffset;
            for (var i = 0; i < count; i++)
            {
                var offset = BitConverter.ToUInt32(blob, 8 + 4 * i);
                if (offset >= blob.Length) continue;
                var full = ILCombine(parent, buffer + (int)offset);
                if (full == IntPtr.Zero) continue;
                try
                {
                    var path = new char[32768];
                    if (SHGetPathFromIDListEx(full, path, path.Length, 0))
                    {
                        var s = new string(path, 0, Array.IndexOf(path, '\0') is var n and >= 0 ? n : path.Length);
                        if (s.Length > 0) result.Add(s);
                    }
                }
                finally
                {
                    ILFree(full);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr pidl, [Out] char[] path, int cchPath, int flags);
}
