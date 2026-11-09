using System.IO;
using System.Runtime.InteropServices;
using DownloadDock.Interop;

namespace DownloadDock.Services;

internal static class KnownFolders
{
    /// <summary>
    /// Resolves the user's Downloads folder through SHGetKnownFolderPath(FOLDERID_Downloads), which
    /// honours redirection (OneDrive, another drive, Group Policy) instead of assuming %USERPROFILE%\Downloads.
    /// </summary>
    public static string GetDownloadsFolder()
    {
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(ShellGuids.FOLDERID_Downloads, 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    NativeMethods.CoTaskMemFree(p);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
