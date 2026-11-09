using System.IO;
using System.Windows.Input;
using DownloadDock.Core.Settings;
using DownloadDock.ViewModels;

namespace DownloadDock.Settings;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly string _defaultFolder;
    private string _folder;
    private int _visibleFiles;
    private int _thumbnailSize;
    private AnimationSpeed _animationSpeed;
    private ThemePreference _theme;
    private bool _startWithWindows;
    private bool _showExtensions;
    private bool _includeSubfolders;
    private bool _closeAfterDrop;
    private readonly AppSettings _original;

    public event Action<AppSettings>? Saved;
    public event Action? CloseRequested;
    public event Action? ClearCacheRequested;
    public event Func<string, string?>? BrowseRequested;

    private bool _screenshotsEnabled;
    private string _fullScreenShortcut;
    private string _regionShortcut;
    private string _screenshotFolder;
    private ScreenshotSaveMode _saveMode;
    private int _thumbnailSeconds;
    private bool _detectWindows;
    private string? _recordingTarget;
    private string? _shortcutMessage;
    private readonly string _defaultScreenshotFolder;
    private readonly Screenshots.HotkeyService? _hotkeys;

    public event Func<string, string?>? BrowseScreenshotFolderRequested;

    public SettingsViewModel(AppSettings settings, string defaultFolder)
        : this(settings, defaultFolder, "", null, null)
    {
    }

    internal SettingsViewModel(AppSettings settings, string defaultFolder, string defaultScreenshotFolder,
        Screenshots.HotkeyService? hotkeys, IReadOnlyDictionary<string, string>? shortcutProblems)
    {
        _defaultScreenshotFolder = defaultScreenshotFolder;
        _hotkeys = hotkeys;
        _screenshotsEnabled = settings.ScreenshotsEnabled;
        _fullScreenShortcut = settings.FullScreenShortcut;
        _regionShortcut = settings.RegionShortcut;
        _screenshotFolder = settings.ScreenshotFolder ?? "";
        _saveMode = settings.ScreenshotSaveMode;
        _thumbnailSeconds = settings.ScreenshotThumbnailSeconds;
        _detectWindows = settings.DetectWindowsScreenshots;
        if (shortcutProblems is { Count: > 0 }) _shortcutMessage = string.Join(" ", shortcutProblems.Values);
        RecordFullScreenCommand = new RelayCommand(() => StartRecording(Screenshots.ScreenshotController.FullScreenId), () => _hotkeys is not null);
        RecordRegionCommand = new RelayCommand(() => StartRecording(Screenshots.ScreenshotController.RegionId), () => _hotkeys is not null);
        ClearFullScreenCommand = new RelayCommand(() => FullScreenShortcut = "");
        ClearRegionCommand = new RelayCommand(() => RegionShortcut = "");
        ResetShortcutsCommand = new RelayCommand(() =>
        {
            FullScreenShortcut = AppSettings.DefaultFullScreenShortcut;
            RegionShortcut = AppSettings.DefaultRegionShortcut;
            ShortcutMessage = null;
        });
        BrowseScreenshotFolderCommand = new RelayCommand(() =>
        {
            if (BrowseScreenshotFolderRequested?.Invoke(EffectiveScreenshotFolder) is { Length: > 0 } picked)
                ScreenshotFolder = string.Equals(picked, _defaultScreenshotFolder, StringComparison.OrdinalIgnoreCase) ? "" : picked;
        });
        ResetScreenshotFolderCommand = new RelayCommand(() => ScreenshotFolder = "");

        _original = settings;
        _defaultFolder = defaultFolder;
        _folder = settings.DownloadsFolder ?? "";
        _visibleFiles = settings.VisibleFiles;
        _thumbnailSize = settings.ThumbnailSize;
        _animationSpeed = settings.AnimationSpeed;
        _theme = settings.Theme;
        _startWithWindows = settings.StartWithWindows;
        _showExtensions = settings.ShowFileExtensions;
        _includeSubfolders = settings.IncludeSubfolders;
        _closeAfterDrop = settings.CloseAfterDrop;

        BrowseCommand = new RelayCommand(Browse);
        ResetFolderCommand = new RelayCommand(() => Folder = "");
        SaveCommand = new RelayCommand(Save, () => FolderIsValid);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
        ClearCacheCommand = new RelayCommand(() => ClearCacheRequested?.Invoke());
    }

    // ------------------------------------------------------------------ screenshots

    public bool ScreenshotsEnabled { get => _screenshotsEnabled; set => Set(ref _screenshotsEnabled, value); }

    public string FullScreenShortcut
    {
        get => _fullScreenShortcut;
        set { if (Set(ref _fullScreenShortcut, value ?? "")) OnPropertyChanged(nameof(FullScreenShortcutDisplay)); }
    }

    public string RegionShortcut
    {
        get => _regionShortcut;
        set { if (Set(ref _regionShortcut, value ?? "")) OnPropertyChanged(nameof(RegionShortcutDisplay)); }
    }

    public string FullScreenShortcutDisplay => _recordingTarget == Screenshots.ScreenshotController.FullScreenId ? "Press keys… (Esc cancels)" : Display(_fullScreenShortcut);

    public string RegionShortcutDisplay => _recordingTarget == Screenshots.ScreenshotController.RegionId ? "Press keys… (Esc cancels)" : Display(_regionShortcut);

    private static string Display(string s) => string.IsNullOrEmpty(s) ? "None" : s.Replace("+", " + ", StringComparison.Ordinal);

    public string? ShortcutMessage
    {
        get => _shortcutMessage;
        set { if (Set(ref _shortcutMessage, value)) OnPropertyChanged(nameof(HasShortcutMessage)); }
    }

    public bool HasShortcutMessage => !string.IsNullOrEmpty(_shortcutMessage);

    public string ScreenshotFolder
    {
        get => _screenshotFolder;
        set
        {
            if (Set(ref _screenshotFolder, value ?? ""))
            {
                OnPropertyChanged(nameof(EffectiveScreenshotFolder));
                OnPropertyChanged(nameof(UsesDefaultScreenshotFolder));
            }
        }
    }

    public string EffectiveScreenshotFolder => string.IsNullOrWhiteSpace(_screenshotFolder) ? _defaultScreenshotFolder : _screenshotFolder;

    public bool UsesDefaultScreenshotFolder => string.IsNullOrWhiteSpace(_screenshotFolder);

    public bool SaveScreenshotsPermanently
    {
        get => _saveMode == ScreenshotSaveMode.Permanent;
        set
        {
            if (Set(ref _saveMode, value ? ScreenshotSaveMode.Permanent : ScreenshotSaveMode.Temporary, nameof(SaveScreenshotsPermanently)))
                OnPropertyChanged(nameof(SaveScreenshotsTemporarily));
        }
    }

    public bool SaveScreenshotsTemporarily
    {
        get => !SaveScreenshotsPermanently;
        set => SaveScreenshotsPermanently = !value;
    }

    public int ThumbnailSeconds { get => _thumbnailSeconds; set => Set(ref _thumbnailSeconds, Math.Clamp(value, AppSettings.MinThumbnailSeconds, AppSettings.MaxThumbnailSeconds)); }

    public int MinThumbnailSeconds => AppSettings.MinThumbnailSeconds;
    public int MaxThumbnailSeconds => AppSettings.MaxThumbnailSeconds;

    public bool DetectWindowsScreenshots { get => _detectWindows; set => Set(ref _detectWindows, value); }

    public ICommand RecordFullScreenCommand { get; }
    public ICommand RecordRegionCommand { get; }
    public ICommand ClearFullScreenCommand { get; }
    public ICommand ClearRegionCommand { get; }
    public ICommand ResetShortcutsCommand { get; }
    public ICommand BrowseScreenshotFolderCommand { get; }
    public ICommand ResetScreenshotFolderCommand { get; }

    private void StartRecording(string target)
    {
        if (_hotkeys is null) return;
        _recordingTarget = target;
        ShortcutMessage = null;
        RefreshShortcutDisplays();
        _hotkeys.BeginRecording(hk =>
        {
            _hotkeys.EndRecording();
            _recordingTarget = null;
            if (hk.Validate() is { } problem)
            {
                ShortcutMessage = problem;
            }
            else
            {
                var text = hk.ToString();
                var other = target == Screenshots.ScreenshotController.FullScreenId ? _regionShortcut : _fullScreenShortcut;
                if (string.Equals(text, other, StringComparison.OrdinalIgnoreCase))
                    ShortcutMessage = $"{text} is already used for the other capture.";
                else if (target == Screenshots.ScreenshotController.FullScreenId) FullScreenShortcut = text;
                else RegionShortcut = text;
            }
            RefreshShortcutDisplays();
        }, () =>
        {
            _hotkeys.EndRecording();
            _recordingTarget = null;
            RefreshShortcutDisplays();
        });
    }

    /// <summary>Stops a pending shortcut recording (window closing).</summary>
    public void CancelRecording()
    {
        if (_recordingTarget is null) return;
        _hotkeys?.EndRecording();
        _recordingTarget = null;
        RefreshShortcutDisplays();
    }

    private void RefreshShortcutDisplays()
    {
        OnPropertyChanged(nameof(FullScreenShortcutDisplay));
        OnPropertyChanged(nameof(RegionShortcutDisplay));
    }

    public string DefaultFolder => _defaultFolder;

    /// <summary>Empty = the Windows Downloads known folder.</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            if (Set(ref _folder, value ?? ""))
            {
                OnPropertyChanged(nameof(EffectiveFolder));
                OnPropertyChanged(nameof(FolderIsValid));
                OnPropertyChanged(nameof(UsesDefaultFolder));
            }
        }
    }

    public string EffectiveFolder => string.IsNullOrWhiteSpace(_folder) ? _defaultFolder : _folder;

    public bool UsesDefaultFolder => string.IsNullOrWhiteSpace(_folder);

    public bool FolderIsValid => Directory.Exists(EffectiveFolder);

    public int VisibleFiles { get => _visibleFiles; set => Set(ref _visibleFiles, Math.Clamp(value, AppSettings.MinVisibleFiles, AppSettings.MaxVisibleFiles)); }

    public int ThumbnailSize { get => _thumbnailSize; set => Set(ref _thumbnailSize, Math.Clamp(value, AppSettings.MinThumbnailSize, AppSettings.MaxThumbnailSize)); }

    public int MinVisibleFiles => AppSettings.MinVisibleFiles;
    public int MaxVisibleFiles => AppSettings.MaxVisibleFiles;
    public int MinThumbnailSize => AppSettings.MinThumbnailSize;
    public int MaxThumbnailSize => AppSettings.MaxThumbnailSize;

    public AnimationSpeed AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, value); }

    public ThemePreference Theme { get => _theme; set => Set(ref _theme, value); }

    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    public bool ShowFileExtensions { get => _showExtensions; set => Set(ref _showExtensions, value); }

    public bool IncludeSubfolders { get => _includeSubfolders; set => Set(ref _includeSubfolders, value); }

    public bool CloseAfterDrop { get => _closeAfterDrop; set => Set(ref _closeAfterDrop, value); }

    public IReadOnlyList<AnimationSpeed> AnimationSpeeds { get; } = Enum.GetValues<AnimationSpeed>();

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();

    public ICommand BrowseCommand { get; }
    public ICommand ResetFolderCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearCacheCommand { get; }

    public AppSettings ToSettings() => (_original with
    {
        DownloadsFolder = string.IsNullOrWhiteSpace(_folder) ? null : _folder,
        VisibleFiles = _visibleFiles,
        ThumbnailSize = _thumbnailSize,
        AnimationSpeed = _animationSpeed,
        Theme = _theme,
        StartWithWindows = _startWithWindows,
        ShowFileExtensions = _showExtensions,
        IncludeSubfolders = _includeSubfolders,
        CloseAfterDrop = _closeAfterDrop,
        ScreenshotsEnabled = _screenshotsEnabled,
        FullScreenShortcut = _fullScreenShortcut,
        RegionShortcut = _regionShortcut,
        ScreenshotFolder = string.IsNullOrWhiteSpace(_screenshotFolder) ? null : _screenshotFolder,
        ScreenshotSaveMode = _saveMode,
        ScreenshotThumbnailSeconds = _thumbnailSeconds,
        DetectWindowsScreenshots = _detectWindows,
    }).Normalize();

    private void Browse()
    {
        var picked = BrowseRequested?.Invoke(EffectiveFolder);
        if (!string.IsNullOrEmpty(picked))
            Folder = string.Equals(picked, _defaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : picked;
    }

    private void Save()
    {
        Saved?.Invoke(ToSettings());
        CloseRequested?.Invoke();
    }
}
