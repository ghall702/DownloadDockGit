using System.Diagnostics;
using System.IO;
using DownloadDock.Core.Integration;
using DownloadDock.Integration;
using DownloadDock.Interop;
using DownloadDock.Services;

namespace DownloadDock.Integration;

/// <summary>Windows implementation of the Finder connector's environment.</summary>
internal sealed class WindowsFinderEnvironment : IFinderEnvironment
{
    /// <summary>Existence checks on an offline share can hang for ~30 s; give up after this.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public AppRegistration? FinderRegistration() => AppRegistrationStore.ReadFinder();

    public bool DirectoryExists(string path) => Probe(() => Directory.Exists(path));

    public bool FileExists(string path) => Probe(() => File.Exists(path));

    private static bool Probe(Func<bool> check)
    {
        var task = Task.Run(check);
        try
        {
            return task.Wait(ProbeTimeout) && task.Result;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    public bool LaunchFinder(string executablePath, IReadOnlyList<string> arguments)
    {
        // Only the registered, validated executable is ever started, with fixed switches + one path.
        if (!AppRegistrationStore.IsAcceptableExecutable(executablePath)) return false;
        try
        {
            var psi = new ProcessStartInfo(executablePath) { UseShellExecute = false };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Services.Log.Error("Starting Finder failed", ex);
            return false;
        }
    }

    public bool OpenInExplorer(string folder)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(folder);
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Services.Log.Error("Opening File Explorer failed", ex);
            return false;
        }
    }

    public bool RevealInExplorer(string path)
    {
        // SHOpenFolderAndSelectItems copes with spaces, Unicode, UNC and cloud-drive paths, unlike
        // hand-built "explorer /select," command lines. It needs COM, so run it on an STA thread.
        var ok = false;
        var thread = new Thread(() =>
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                if (NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    ok = NativeMethods.SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
            }
            finally
            {
                if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        if (ok) return true;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Services.Log.Error("Revealing in File Explorer failed", ex);
            return false;
        }
    }

    public void Log(string message) => Services.Log.Info(message);
}
