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

    public SettingsViewModel(AppSettings settings, string defaultFolder)
    {
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
