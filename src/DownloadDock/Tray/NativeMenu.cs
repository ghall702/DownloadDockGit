using DownloadDock.Interop;

namespace DownloadDock.Tray;

/// <summary>A native Win32 popup menu (gets the Windows 11 rounded, themed menu look for free).</summary>
internal sealed class NativeMenu
{
    public sealed record Item(int Id, string? Text, bool IsDefault = false, bool IsChecked = false, bool Enabled = true)
    {
        public static Item Separator => new(0, null);
    }

    private readonly List<Item> _items = new();

    public NativeMenu Add(int id, string text, bool isDefault = false, bool isChecked = false, bool enabled = true)
    {
        _items.Add(new Item(id, text, isDefault, isChecked, enabled));
        return this;
    }

    public NativeMenu AddSeparator()
    {
        _items.Add(Item.Separator);
        return this;
    }

    /// <summary>Shows the menu at a screen point and returns the chosen id (0 = dismissed).</summary>
    public int Show(IntPtr owner, int x, int y, bool darkMode, bool alignAboveCursor = true)
    {
        TryApplyMenuTheme(darkMode);
        var menu = NativeMethods.CreatePopupMenu();
        try
        {
            foreach (var item in _items)
            {
                if (item.Text is null)
                {
                    NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }
                var flags = NativeMethods.MF_STRING;
                if (item.IsDefault) flags |= NativeMethods.MF_DEFAULT;
                if (item.IsChecked) flags |= NativeMethods.MF_CHECKED;
                if (!item.Enabled) flags |= NativeMethods.MF_GRAYED;
                NativeMethods.AppendMenuW(menu, flags, (IntPtr)item.Id, item.Text);
            }

            // Required so the menu closes when the user clicks elsewhere (KB135788).
            NativeMethods.SetForegroundWindow(owner);
            var flags2 = NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RIGHTBUTTON;
            if (alignAboveCursor) flags2 |= NativeMethods.TPM_BOTTOMALIGN | NativeMethods.TPM_RIGHTALIGN;
            var cmd = NativeMethods.TrackPopupMenuEx(menu, flags2, x, y, owner, IntPtr.Zero);
            NativeMethods.PostMessageW(owner, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static void TryApplyMenuTheme(bool dark)
    {
        try
        {
            // 2 = ForceDark, 3 = ForceLight (uxtheme ordinal 135, available since Windows 10 1903).
            NativeMethods.SetPreferredAppMode(dark ? 2 : 3);
            NativeMethods.FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }
}
