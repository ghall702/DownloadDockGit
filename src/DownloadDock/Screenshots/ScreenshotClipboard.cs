using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DownloadDock.Interop;
using DownloadDock.Services;
using Microsoft.Win32;

namespace DownloadDock.Screenshots;

/// <summary>Explicit, user-requested Copy and Save As for screenshots (DownloadDock never touches the clipboard otherwise).</summary>
internal static class ScreenshotClipboard
{
    /// <summary>Puts the image on the clipboard as a bitmap, as PNG (keeps quality in Office/browsers) and as the file.</summary>
    public static void Copy(BitmapSource image, string? file)
    {
        try
        {
            var data = new DataObject();
            data.SetImage(image);
            var png = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            enc.Save(png);
            png.Position = 0;
            data.SetData("PNG", png);
            if (file is not null && File.Exists(file))
            {
                data.SetFileDropList(new StringCollection { file });
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(NativeMethods.DROPEFFECT_COPY)));
            }
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("Copying the screenshot failed", ex);
        }
    }

    /// <summary>Asks where to save and writes PNG or JPEG. Returns the path, or null if cancelled.</summary>
    public static string? SaveAs(BitmapSource image, string? currentFile, Window? owner = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = currentFile is null ? Core.Screenshots.ScreenshotNaming.BaseName(DateTime.Now) : Path.GetFileNameWithoutExtension(currentFile),
            InitialDirectory = currentFile is null ? ScreenshotStore.WindowsScreenshotsFolder() : Path.GetDirectoryName(currentFile),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(owner) != true) return null;
        Write(image, dialog.FileName);
        return dialog.FileName;
    }

    public static void Write(BitmapSource image, string path)
    {
        BitmapEncoder enc = Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        var tmp = path + ".partial";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, overwrite: true);
    }
}
