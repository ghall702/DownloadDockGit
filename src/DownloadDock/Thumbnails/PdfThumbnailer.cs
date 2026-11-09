using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadDock.Interop;

namespace DownloadDock.Thumbnails;

/// <summary>
/// Renders the first page of a PDF with PDFium. Works without Adobe Acrobat or any other PDF
/// software. The file is streamed through a callback, so very large PDFs are not loaded into memory.
/// </summary>
internal static class PdfThumbnailer
{
    // Kept in a static field so the GC never collects the delegate while PDFium holds its pointer.
    private static readonly Pdfium.GetBlockCallback GetBlock = ReadBlock;
    private static readonly IntPtr GetBlockPtr = Marshal.GetFunctionPointerForDelegate(GetBlock);

    public static BitmapSource? Render(string path, int maxPixels)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        if (fs.Length == 0 || fs.Length > uint.MaxValue) return null;

        var handle = GCHandle.Alloc(fs);
        var access = Marshal.AllocHGlobal(Marshal.SizeOf<Pdfium.FPDF_FILEACCESS>());
        try
        {
            Marshal.StructureToPtr(new Pdfium.FPDF_FILEACCESS
            {
                m_FileLen = (uint)fs.Length,
                m_GetBlock = GetBlockPtr,
                m_Param = GCHandle.ToIntPtr(handle),
            }, access, false);

            lock (Pdfium.Sync)
            {
                Pdfium.EnsureInitialized();
                var doc = LoadCustomDocument(access);
                if (doc == IntPtr.Zero) return null; // encrypted, damaged or not a PDF
                try
                {
                    if (Pdfium.FPDF_GetPageCount(doc) <= 0) return null;
                    var page = Pdfium.FPDF_LoadPage(doc, 0);
                    if (page == IntPtr.Zero) return null;
                    try
                    {
                        return RenderPage(page, maxPixels);
                    }
                    finally
                    {
                        Pdfium.FPDF_ClosePage(page);
                    }
                }
                finally
                {
                    Pdfium.FPDF_CloseDocument(doc);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(access);
            handle.Free();
        }
    }

    [DllImport("pdfium", EntryPoint = "FPDF_LoadCustomDocument", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr LoadCustomDocument(IntPtr pFileAccess, IntPtr password = default);

    private static BitmapSource? RenderPage(IntPtr page, int maxPixels)
    {
        double pw = Pdfium.FPDF_GetPageWidthF(page);
        double ph = Pdfium.FPDF_GetPageHeightF(page);
        if (pw <= 0 || ph <= 0) return null;
        var scale = maxPixels / Math.Max(pw, ph);
        var w = Math.Max(1, (int)Math.Round(pw * scale));
        var h = Math.Max(1, (int)Math.Round(ph * scale));

        var bitmap = Pdfium.FPDFBitmap_Create(w, h, 1);
        if (bitmap == IntPtr.Zero) return null;
        try
        {
            Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
            Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, Pdfium.FPDF_ANNOT | Pdfium.FPDF_RENDER_LIMITEDIMAGECACHE);
            var buffer = Pdfium.FPDFBitmap_GetBuffer(bitmap);
            var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buffer, stride * h, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            Pdfium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private static int ReadBlock(IntPtr param, uint position, IntPtr buffer, uint size)
    {
        try
        {
            if (GCHandle.FromIntPtr(param).Target is not FileStream fs) return 0;
            fs.Position = position;
            unsafe
            {
                var span = new Span<byte>((void*)buffer, checked((int)size));
                var total = 0;
                while (total < span.Length)
                {
                    var n = fs.Read(span[total..]);
                    if (n <= 0) return 0;
                    total += n;
                }
            }
            return 1;
        }
        catch
        {
            return 0; // never let an exception unwind through native PDFium frames
        }
    }
}
