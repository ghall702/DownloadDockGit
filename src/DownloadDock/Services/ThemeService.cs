using System.Windows.Media;
using DownloadDock.Core.Settings;
using Microsoft.Win32;

namespace DownloadDock.Services;

/// <summary>Reads Windows light/dark preferences and the accent colour.</summary>
internal static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when apps should use the dark theme.</summary>
    public static bool AppsUseDarkTheme() => ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;

    /// <summary>True when the taskbar / system UI is dark (tray icon glyph should be light).</summary>
    public static bool SystemUsesDarkTheme() => ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) == 0;

    public static bool IsDark(ThemePreference preference) => preference switch
    {
        ThemePreference.Dark => true,
        ThemePreference.Light => false,
        _ => AppsUseDarkTheme(),
    };

    public static Color AccentColor()
    {
        // DWM stores the accent as 0xAABBGGRR.
        var v = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFC06700));
        var abgr = unchecked((uint)v);
        return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
    }

    private static int ReadDword(string path, string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) is int i ? i : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return fallback;
        }
    }
}
