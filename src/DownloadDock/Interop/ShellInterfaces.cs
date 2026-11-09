using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace DownloadDock.Interop;

internal static class ShellGuids
{
    /// <summary>BHID_DataObject – asks the shell for the same IDataObject Explorer uses when you drag a file.</summary>
    public static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    public static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    public static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
}

[Flags]
internal enum SIIGBF
{
    ResizeToFit = 0x00,
    BiggerSizeOk = 0x01,
    MemoryOnly = 0x02,
    IconOnly = 0x04,
    ThumbnailOnly = 0x08,
    InCacheOnly = 0x10,
    CropToSquare = 0x20,
    WideThumbnails = 0x40,
    IconBackground = 0x80,
    ScaleUp = 0x100,
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [PreserveSig]
    int GetParent(out IShellItem ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out IntPtr ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvOut);

    // Remaining methods are not used; declared to keep the vtable shape correct.
    [PreserveSig] int GetPropertyStore(int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport]
[Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDragSourceHelper
{
    [PreserveSig]
    int InitializeFromBitmap(ref SHDRAGIMAGE pshdi, ComTypes.IDataObject pDataObject);

    [PreserveSig]
    int InitializeFromWindow(IntPtr hwnd, ref POINT ppt, ComTypes.IDataObject pDataObject);
}

[ComImport]
[Guid("83E07D0D-0C5F-4163-BF1A-60B274051E40")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDragSourceHelper2
{
    [PreserveSig]
    int InitializeFromBitmap(ref SHDRAGIMAGE pshdi, ComTypes.IDataObject pDataObject);

    [PreserveSig]
    int InitializeFromWindow(IntPtr hwnd, ref POINT ppt, ComTypes.IDataObject pDataObject);

    [PreserveSig]
    int SetFlags(int dwFlags);
}

[ComImport]
[Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
[ClassInterface(ClassInterfaceType.None)]
internal class DragDropHelperClass
{
}
