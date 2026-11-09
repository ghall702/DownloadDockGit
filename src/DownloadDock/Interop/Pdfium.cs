using System.Runtime.InteropServices;

namespace DownloadDock.Interop;

/// <summary>
/// Minimal P/Invoke surface of PDFium (pdfium.dll from the bblanchon.PDFium.Win32 package,
/// BSD-3-Clause). PDFium is not thread-safe, so every call must hold <see cref="Sync"/>.
/// </summary>
internal static class Pdfium
{
    private const string Lib = "pdfium";
    public static readonly object Sync = new();
    private static bool _initialized;

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_NO_CATCH = 0x100;
    public const int FPDF_RENDER_LIMITEDIMAGECACHE = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public uint m_FileLen;           // unsigned long (32-bit on Windows)
        public IntPtr m_GetBlock;        // int (*)(void* param, unsigned long position, unsigned char* pBuf, unsigned long size)
        public IntPtr m_Param;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetBlockCallback(IntPtr param, uint position, IntPtr buffer, uint size);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDF_LoadCustomDocument(ref FPDF_FILEACCESS pFileAccess, [MarshalAs(UnmanagedType.LPStr)] string? password);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDF_GetLastError();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>Initialises the library once. Caller must hold <see cref="Sync"/>.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        FPDF_InitLibrary();
        _initialized = true;
    }
}
