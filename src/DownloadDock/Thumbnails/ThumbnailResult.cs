using System.Windows.Media.Imaging;

namespace DownloadDock.Thumbnails;

/// <summary>A rendered preview. <see cref="IsIcon"/> is true when only a file-type icon was available.</summary>
public sealed record ThumbnailResult(BitmapSource Image, bool IsIcon, string Source);
