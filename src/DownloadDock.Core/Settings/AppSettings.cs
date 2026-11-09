namespace DownloadDock.Core.Settings;

public enum ThemePreference { System, Light, Dark }

public enum AnimationSpeed { Off, Fast, Normal, Slow }

/// <summary>What happens to a screenshot file after capture.</summary>
public enum ScreenshotSaveMode
{
    /// <summary>Saved straight into the screenshot folder (like macOS).</summary>
    Permanent,
    /// <summary>Kept in a temporary folder (cleaned up after a day) until you choose Save.</summary>
    Temporary,
}

/// <summary>User settings persisted as JSON in %APPDATA%\DownloadDock\settings.json.</summary>
public sealed record AppSettings
{
    public const int MinVisibleFiles = 3;
    public const int MaxVisibleFiles = 20;
    public const int MinThumbnailSize = 64;
    public const int MaxThumbnailSize = 160;

    /// <summary>Folder to monitor; null/empty = the Windows Downloads known folder.</summary>
    public string? DownloadsFolder { get; init; }

    public int VisibleFiles { get; init; } = 9;

    /// <summary>Thumbnail box size in DIPs.</summary>
    public int ThumbnailSize { get; init; } = 96;

    public AnimationSpeed AnimationSpeed { get; init; } = AnimationSpeed.Normal;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public bool StartWithWindows { get; init; } = true;

    public bool ShowFileExtensions { get; init; } = true;

    public bool IncludeSubfolders { get; init; }

    /// <summary>Close the fan after a successful drag-and-drop (macOS behaviour).</summary>
    public bool CloseAfterDrop { get; init; } = true;

    // ---------------------------------------------------------------- screenshots

    public const int MinThumbnailSeconds = 3;
    public const int MaxThumbnailSeconds = 60;
    public const string DefaultFullScreenShortcut = "Win+Shift+3";
    public const string DefaultRegionShortcut = "Win+Shift+4";

    /// <summary>Master switch for the screenshot feature (shortcuts and floating thumbnails).</summary>
    public bool ScreenshotsEnabled { get; init; } = true;

    /// <summary>Global shortcut for a full-screen capture (empty = none).</summary>
    public string FullScreenShortcut { get; init; } = DefaultFullScreenShortcut;

    /// <summary>Global shortcut for a rectangular selection (empty = none).</summary>
    public string RegionShortcut { get; init; } = DefaultRegionShortcut;

    /// <summary>Where screenshots are saved; null = the Windows "Screenshots" known folder (Pictures\Screenshots).</summary>
    public string? ScreenshotFolder { get; init; }

    public ScreenshotSaveMode ScreenshotSaveMode { get; init; } = ScreenshotSaveMode.Permanent;

    /// <summary>How long the floating thumbnail stays on screen.</summary>
    public int ScreenshotThumbnailSeconds { get; init; } = 10;

    /// <summary>Also show the thumbnail for Snipping Tool, Win+Shift+S and Print Screen captures.</summary>
    public bool DetectWindowsScreenshots { get; init; } = true;

    /// <summary>Returns a copy with every value forced into its valid range.</summary>
    public AppSettings Normalize() => this with
    {
        VisibleFiles = Math.Clamp(VisibleFiles, MinVisibleFiles, MaxVisibleFiles),
        ThumbnailSize = Math.Clamp(ThumbnailSize, MinThumbnailSize, MaxThumbnailSize),
        AnimationSpeed = Enum.IsDefined(AnimationSpeed) ? AnimationSpeed : AnimationSpeed.Normal,
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
        DownloadsFolder = string.IsNullOrWhiteSpace(DownloadsFolder) ? null : DownloadsFolder.Trim(),
        ScreenshotFolder = string.IsNullOrWhiteSpace(ScreenshotFolder) ? null : ScreenshotFolder.Trim(),
        ScreenshotSaveMode = Enum.IsDefined(ScreenshotSaveMode) ? ScreenshotSaveMode : ScreenshotSaveMode.Permanent,
        ScreenshotThumbnailSeconds = Math.Clamp(ScreenshotThumbnailSeconds, MinThumbnailSeconds, MaxThumbnailSeconds),
        FullScreenShortcut = NormalizeShortcut(FullScreenShortcut, DefaultFullScreenShortcut),
        RegionShortcut = NormalizeShortcut(RegionShortcut, DefaultRegionShortcut),
    };

    private static string NormalizeShortcut(string? value, string fallback)
    {
        if (value is null) return fallback;
        if (value.Trim().Length == 0) return "";
        return Screenshots.Hotkey.TryParse(value, out var hk) ? hk.ToString() : fallback;
    }

    /// <summary>Multiplier applied to all animation durations (0 = no animation).</summary>
    public static double AnimationFactor(AnimationSpeed speed) => speed switch
    {
        AnimationSpeed.Off => 0,
        AnimationSpeed.Fast => 0.6,
        AnimationSpeed.Slow => 1.7,
        _ => 1.0,
    };
}
