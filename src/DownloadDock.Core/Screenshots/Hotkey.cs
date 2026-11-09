using System.Globalization;

namespace DownloadDock.Core.Screenshots;

/// <summary>Modifier flags. Values match the Win32 MOD_* constants used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global keyboard shortcut such as "Win+Shift+3" (virtual-key based, layout independent).</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public const int VK_SNAPSHOT = 0x2C;

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrintScreen"] = VK_SNAPSHOT, ["PrtScn"] = VK_SNAPSHOT, ["Space"] = 0x20, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Pause"] = 0x13,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
    };

    /// <summary>Shortcuts Windows reserves for itself or that would break screenshots the user already relies on.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Win+Shift+S", "PrintScreen", "Win+PrintScreen", "Alt+PrintScreen", "Win+L", "Win+D", "Win+E", "Win+R",
        "Win+Tab", "Control+Alt+Delete", "Win+G", "Win+Alt+PrintScreen", "Win+Shift+R",
    };

    public bool IsEmpty => VirtualKey == 0;

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        int? vk = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win": case "windows": case "meta": mods |= HotkeyModifiers.Win; continue;
                case "shift": mods |= HotkeyModifiers.Shift; continue;
                case "ctrl": case "control": mods |= HotkeyModifiers.Control; continue;
                case "alt": mods |= HotkeyModifiers.Alt; continue;
            }
            if (vk is not null) return false; // two non-modifier keys
            vk = ParseKey(raw);
            if (vk is null) return false;
        }
        if (vk is null) return false;
        hotkey = new Hotkey(mods, vk.Value);
        return true;
    }

    private static int? ParseKey(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return null;
        }
        if ((key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        return NamedKeys.TryGetValue(key, out var vk) ? vk : null;
    }

    public static string KeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var (name, code) in NamedKeys)
        {
            if (code == vk && name != "PrtScn") return name;
        }
        return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>True for keys the recorder can't express as a shortcut on their own.</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>Explains why a shortcut can't be used, or returns null when it's acceptable.</summary>
    public string? Validate()
    {
        if (IsEmpty) return "Press a key combination.";
        var canonical = ToString().Replace("Ctrl", "Control", StringComparison.Ordinal);
        if (Reserved.Contains(canonical) || Reserved.Contains(ToString()))
            return $"{ToString()} is reserved by Windows.";
        var isFunctionKey = VirtualKey is >= 0x70 and <= 0x87;
        if (Modifiers == HotkeyModifiers.None && !isFunctionKey)
            return "Use at least one modifier (Win, Ctrl, Alt or Shift).";
        if (Modifiers == HotkeyModifiers.Shift && !isFunctionKey)
            return "Shift alone would block normal typing; add Win, Ctrl or Alt.";
        return null;
    }
}
