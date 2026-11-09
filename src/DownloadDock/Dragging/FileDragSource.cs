using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadDock.Interop;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace DownloadDock.Dragging;

/// <summary>Outcome of a drag operation.</summary>
public enum FileDragResult
{
    Dropped,
    Cancelled,
    Failed,
}

/// <summary>
/// Starts a genuine OLE drag-and-drop of real files.
///
/// The data object is obtained from the Windows Shell itself (IShellItemArray → BHID_DataObject),
/// i.e. exactly the object File Explorer puts on a drag. It exposes CF_HDROP, "Shell IDList Array",
/// FileNameW and friends, so every target that accepts a drag from Explorer – Chrome/Edge upload
/// areas (Gmail, Canva), Spark, Outlook, PowerPoint, Word, Explorer itself – accepts it too.
///
/// Only DROPEFFECT_COPY | DROPEFFECT_LINK are offered and "Preferred DropEffect" is set to COPY,
/// so no target can move or delete the original download.
/// </summary>
public static class FileDragSource
{
    /// <summary>Raised on the UI thread when a drag from DownloadDock starts.</summary>
    public static event Action? DragStarted;

    /// <summary>
    /// Raised continuously during a drag from DownloadDock with the pointer position (physical pixels).
    /// Used to spring-load the fan when a dragged screenshot rests on the tray icon.
    /// </summary>
    internal static event Action<POINT>? DragMoved;

    /// <summary>Raised when a drag from DownloadDock has finished.</summary>
    public static event Action<FileDragResult>? DragEnded;

    /// <summary>True while a drag started by DownloadDock is in progress.</summary>
    public static bool IsDragInProgress { get; private set; }

    private static readonly int CfPreferredDropEffect = NativeMethods.RegisterClipboardFormatW("Preferred DropEffect");
    private const int DSH_ALLOWDROPDESCRIPTIONTEXT = 0x0001;
    private const int AllowedEffects = NativeMethods.DROPEFFECT_COPY | NativeMethods.DROPEFFECT_LINK;

    /// <summary>
    /// Performs the drag. Blocks (pumping messages) until the user drops or cancels – the caller's
    /// UI keeps rendering because OLE runs a modal message loop on this STA thread.
    /// </summary>
    /// <param name="ownerHwnd">Window that starts the drag (used for drag-image positioning).</param>
    /// <param name="paths">Files to drag.</param>
    /// <param name="dragImage">Optional image shown under the cursor (premultiplied alpha supported).</param>
    /// <param name="cursorOffsetPx">Position of the cursor inside <paramref name="dragImage"/>, in pixels.</param>
    public static FileDragResult DoDragDrop(IntPtr ownerHwnd, IReadOnlyList<string> paths, BitmapSource? dragImage, Point cursorOffsetPx, out int effect)
    {
        effect = NativeMethods.DROPEFFECT_NONE;
        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length == 0) return FileDragResult.Failed;

        var dataObject = CreateDataObject(existing);
        if (dataObject is null) return FileDragResult.Failed;

        try
        {
            SetPreferredDropEffect(dataObject, NativeMethods.DROPEFFECT_COPY);
            if (dragImage is not null)
                TrySetDragImage(dataObject, dragImage, cursorOffsetPx);

            // Our own IDropSource behaves exactly like the default one (Esc cancels, releasing the button
            // drops, standard cursors) but also reports the pointer position while dragging.
            var source = new DropSource();
            var sourcePtr = Marshal.GetComInterfaceForObject(source, typeof(IOleDropSource));
            IsDragInProgress = true;
            FileDragResult result;
            try
            {
                DragStarted?.Invoke();
                var hr = NativeMethods.SHDoDragDropWithSource(ownerHwnd, dataObject, sourcePtr, AllowedEffects, out effect);
                Debug.WriteLine($"SHDoDragDrop hr=0x{hr:X8} effect={effect}");
                if (hr == NativeMethods.DRAGDROP_S_DROP && effect != NativeMethods.DROPEFFECT_NONE)
                    result = FileDragResult.Dropped;
                else if (hr == NativeMethods.DRAGDROP_S_DROP || hr == NativeMethods.DRAGDROP_S_CANCEL || hr == 0)
                    result = effect != NativeMethods.DROPEFFECT_NONE ? FileDragResult.Dropped : FileDragResult.Cancelled;
                else
                    result = FileDragResult.Failed;
            }
            finally
            {
                IsDragInProgress = false;
                Marshal.Release(sourcePtr);
            }
            DragEnded?.Invoke(result);
            return result;
        }
        finally
        {
            if (Marshal.IsComObject(dataObject))
                Marshal.ReleaseComObject(dataObject);
        }
    }

    [ComImport]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleDropSource
    {
        [PreserveSig] int QueryContinueDrag(int fEscapePressed, int grfKeyState);
        [PreserveSig] int GiveFeedback(int dwEffect);
    }

    /// <summary>Standard drop-source behaviour plus pointer reporting.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class DropSource : IOleDropSource
    {
        private const int MK_LBUTTON = 0x1, MK_RBUTTON = 0x2;
        private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
        private int _initialButtons = -1;

        public int QueryContinueDrag(int fEscapePressed, int grfKeyState)
        {
            if (_initialButtons < 0) _initialButtons = grfKeyState & (MK_LBUTTON | MK_RBUTTON);
            if (fEscapePressed != 0) return NativeMethods.DRAGDROP_S_CANCEL;
            if ((grfKeyState & (MK_LBUTTON | MK_RBUTTON)) == 0 || (_initialButtons != 0 && (grfKeyState & _initialButtons) == 0))
                return NativeMethods.DRAGDROP_S_DROP;
            try
            {
                if (NativeMethods.GetCursorPos(out var p)) DragMoved?.Invoke(p);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DragMoved handler failed: " + ex.Message); // never break the drag
            }
            return 0;
        }

        public int GiveFeedback(int dwEffect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    /// <summary>
    /// Creates the drag data object. Prefers the Shell's own data object; falls back to a
    /// WPF <see cref="DataObject"/> carrying CF_HDROP (FileDrop) if the Shell can't provide one
    /// (e.g. unusual namespace extensions).
    /// </summary>
    internal static ComTypes.IDataObject? CreateDataObject(IReadOnlyList<string> paths)
        => CreateShellDataObject(paths) ?? CreateFallbackDataObject(paths);

    internal static ComTypes.IDataObject? CreateShellDataObject(IReadOnlyList<string> paths)
    {
        var pidls = new IntPtr[paths.Count];
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var hr = NativeMethods.SHParseDisplayName(Path.GetFullPath(paths[i]), IntPtr.Zero, out pidls[i], 0, out _);
                if (hr != 0 || pidls[i] == IntPtr.Zero) return null;
            }

            if (NativeMethods.SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array) != 0 || array is null)
                return null;
            try
            {
                var hr = array.BindToHandler(IntPtr.Zero, ShellGuids.BHID_DataObject, ShellGuids.IID_IDataObject, out var obj);
                return hr == 0 ? obj as ComTypes.IDataObject : null;
            }
            finally
            {
                Marshal.ReleaseComObject(array);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or IOException)
        {
            Debug.WriteLine("Shell data object failed: " + ex.Message);
            return null;
        }
        finally
        {
            foreach (var p in pidls)
                if (p != IntPtr.Zero) NativeMethods.CoTaskMemFree(p);
        }
    }

    internal static ComTypes.IDataObject CreateFallbackDataObject(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());
        // DROPEFFECT_COPY as a 4-byte little-endian DWORD.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(NativeMethods.DROPEFFECT_COPY)));
        return data;
    }

    internal static void SetPreferredDropEffect(ComTypes.IDataObject dataObject, int dropEffect)
    {
        var format = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)CfPreferredDropEffect),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };
        var h = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (UIntPtr)4);
        if (h == IntPtr.Zero) return;
        var p = NativeMethods.GlobalLock(h);
        Marshal.WriteInt32(p, dropEffect);
        NativeMethods.GlobalUnlock(h);
        var medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = h, pUnkForRelease = null! };
        try
        {
            dataObject.SetData(ref format, ref medium, true); // the data object now owns h
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or ArgumentException)
        {
            NativeMethods.GlobalFree(h);
        }
    }

    private static void TrySetDragImage(ComTypes.IDataObject dataObject, BitmapSource image, Point offset)
    {
        IntPtr hbmp = IntPtr.Zero;
        object? helper = null;
        try
        {
            helper = new DragDropHelperClass();
            hbmp = BitmapInterop.CreatePremultipliedHBitmap(image);
            if (hbmp == IntPtr.Zero) return;

            if (helper is IDragSourceHelper2 helper2)
                helper2.SetFlags(DSH_ALLOWDROPDESCRIPTIONTEXT);

            var shdi = new SHDRAGIMAGE
            {
                sizeDragImage = new SIZE(image.PixelWidth, image.PixelHeight),
                ptOffset = new POINT((int)Math.Clamp(offset.X, 0, image.PixelWidth), (int)Math.Clamp(offset.Y, 0, image.PixelHeight)),
                hbmpDragImage = hbmp,
                crColorKey = unchecked((int)0xFFFFFFFF), // CLR_NONE: use the alpha channel
            };
            var hr = ((IDragSourceHelper)helper).InitializeFromBitmap(ref shdi, dataObject);
            if (hr == 0) hbmp = IntPtr.Zero; // ownership transferred to the shell
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine("Drag image failed: " + ex.Message);
        }
        finally
        {
            if (hbmp != IntPtr.Zero) NativeMethods.DeleteObject(hbmp);
            if (helper is not null && Marshal.IsComObject(helper)) Marshal.ReleaseComObject(helper);
        }
    }

    /// <summary>Lists the file paths a data object exposes through CF_HDROP (used by tests and diagnostics).</summary>
    internal static IReadOnlyList<string> ReadHDrop(ComTypes.IDataObject dataObject)
    {
        var format = new ComTypes.FORMATETC
        {
            cfFormat = 15, // CF_HDROP
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };
        dataObject.GetData(ref format, out var medium);
        try
        {
            var count = NativeMethods.DragQueryFileW(medium.unionmember, 0xFFFFFFFF, null, 0);
            var result = new List<string>(count);
            for (uint i = 0; i < count; i++)
            {
                var len = NativeMethods.DragQueryFileW(medium.unionmember, i, null, 0);
                var buf = new char[len + 1];
                NativeMethods.DragQueryFileW(medium.unionmember, i, buf, buf.Length);
                result.Add(new string(buf, 0, len));
            }
            return result;
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>Renders a thumbnail into a drag image with rounded corners and a soft shadow.</summary>
    public static BitmapSource? CreateDragImage(ImageSource? thumbnail, double sizeDip, double dpiScale, bool isIcon)
    {
        if (thumbnail is null) return null;
        var w = thumbnail.Width;
        var h = thumbnail.Height;
        if (w <= 0 || h <= 0) return null;
        var fit = Math.Min(sizeDip / w, sizeDip / h);
        var dw = w * fit;
        var dh = h * fit;
        const double pad = 6;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(pad, pad, dw, dh);
            if (!isIcon)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, new Rect(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), 6, 6);
                dc.PushClip(new RectangleGeometry(rect, 6, 6));
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawImage(thumbnail, rect);
                dc.Pop();
            }
            else
            {
                dc.DrawImage(thumbnail, rect);
            }
        }
        var pw = (int)Math.Ceiling((dw + 2 * pad) * dpiScale);
        var ph = (int)Math.Ceiling((dh + 2 * pad) * dpiScale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
