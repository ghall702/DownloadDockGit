namespace DownloadDock.Core.Settings;

public enum ThemePreference { System, Light, Dark }

public enum AnimationSpeed { Off, Fast, Normal, Slow }

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

    /// <summary>Returns a copy with every value forced into its valid range.</summary>
    public AppSettings Normalize() => this with
    {
        VisibleFiles = Math.Clamp(VisibleFiles, MinVisibleFiles, MaxVisibleFiles),
        ThumbnailSize = Math.Clamp(ThumbnailSize, MinThumbnailSize, MaxThumbnailSize),
        AnimationSpeed = Enum.IsDefined(AnimationSpeed) ? AnimationSpeed : AnimationSpeed.Normal,
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
        DownloadsFolder = string.IsNullOrWhiteSpace(DownloadsFolder) ? null : DownloadsFolder.Trim(),
    };

    /// <summary>Multiplier applied to all animation durations (0 = no animation).</summary>
    public static double AnimationFactor(AnimationSpeed speed) => speed switch
    {
        AnimationSpeed.Off => 0,
        AnimationSpeed.Fast => 0.6,
        AnimationSpeed.Slow => 1.7,
        _ => 1.0,
    };
}
