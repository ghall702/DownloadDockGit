using System.Windows.Media;
using DownloadDock.Core.Downloads;

namespace DownloadDock.ViewModels;

/// <summary>One card in the fan: a downloaded file, or the "Open Downloads Folder" item at the top.</summary>
public sealed class FanItemViewModel : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _isIcon = true;
    private bool _isHighlighted;
    private string _displayName = "";
    private string? _subtitle;
    private double _thumbWidth;
    private double _thumbHeight;
    private DownloadEntry? _entry;

    public FanItemViewModel(double boxSize)
    {
        BoxSize = boxSize;
        ApplyPlaceholderSize();
    }

    public double BoxSize { get; }

    public bool IsFolderItem { get; init; }

    public string? FolderPath { get; init; }

    public DownloadEntry? Entry
    {
        get => _entry;
        set => Set(ref _entry, value);
    }

    public string Path => Entry?.FullPath ?? FolderPath ?? "";

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    public string? Subtitle
    {
        get => _subtitle;
        set
        {
            if (Set(ref _subtitle, value)) OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public bool HasSubtitle => !string.IsNullOrEmpty(_subtitle);

    public string ExtensionLabel => Entry is null ? "" : System.IO.Path.GetExtension(Entry.Name).TrimStart('.').ToUpperInvariant();

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (Set(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>True when the preview is a file-type icon (drawn without the white "page" card).</summary>
    public bool IsIcon { get => _isIcon; private set => Set(ref _isIcon, value); }

    public bool IsHighlighted { get => _isHighlighted; set => Set(ref _isHighlighted, value); }

    /// <summary>Card size: the preview's aspect ratio fitted into the square thumbnail box.</summary>
    public double ThumbWidth { get => _thumbWidth; private set => Set(ref _thumbWidth, value); }

    public double ThumbHeight { get => _thumbHeight; private set => Set(ref _thumbHeight, value); }

    public void SetThumbnail(ImageSource? image, bool isIcon)
    {
        IsIcon = isIcon;
        Thumbnail = image;
        if (image is null || image.Width <= 0 || image.Height <= 0)
        {
            ApplyPlaceholderSize();
            return;
        }
        var box = isIcon ? BoxSize * 0.86 : BoxSize;
        var fit = Math.Min(box / image.Width, box / image.Height);
        // Keep extreme panoramas / receipts recognisable rather than razor-thin.
        ThumbWidth = Math.Max(BoxSize * 0.42, image.Width * fit);
        ThumbHeight = Math.Max(BoxSize * 0.42, image.Height * fit);
    }

    private void ApplyPlaceholderSize()
    {
        ThumbWidth = BoxSize * 0.76;
        ThumbHeight = BoxSize * 0.96;
    }

    public static string FormatName(string fileName, bool showExtension)
        => showExtension ? fileName : System.IO.Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } n ? n : fileName;
}
