using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Fan;
using DownloadDock.Core.Settings;
using DownloadDock.Core.Tests;
using DownloadDock.Fan;
using DownloadDock.Interop;
using DownloadDock.Thumbnails;
using DownloadDock.Tray;
using DownloadDock.ViewModels;

namespace DownloadDock.Windows.Tests;

public class FanWindowTests
{
    private static readonly FanAnchor Anchor = new(new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1032), 1.0, 1590, 1032, FanDirection.Up, true);

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static List<DownloadEntry> Entries(int n)
    {
        var t = new DateTime(2026, 1, 29, 10, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, n).Select(i => new DownloadEntry($@"C:\none\file{i}.pdf", $"file{i}.pdf", 10, t.AddMinutes(-i), t.AddMinutes(-i))).ToList();
    }

    [Fact]
    public void Opens_a_curved_fan_with_one_view_per_file_plus_the_folder_item()
    {
        Sta.Run(() =>
        {
            using var thumbs = new ThumbnailService(Path.Combine(Path.GetTempPath(), "dd-fan-test-" + Guid.NewGuid()));
            var fan = new FanWindow(thumbs);
            var settings = new AppSettings { VisibleFiles = 8, AnimationSpeed = AnimationSpeed.Off };
            fan.Open(Anchor, Entries(20), 20, @"C:\none", settings, dark: false);
            Pump(200);
            try
            {
                var canvas = (Canvas)fan.Content;
                var views = canvas.Children.OfType<FanItemView>().OrderBy(v => v.SlotIndex).ToList();
                Assert.Equal(9, views.Count);
                Assert.True(views[^1].ViewModel.IsFolderItem);
                Assert.Equal("file0.pdf", views[0].ViewModel.DisplayName); // newest closest to the icon
                Assert.Contains("12 more", views[^1].ViewModel.Subtitle);

                // Each item is rotated more than the one below it, and positioned higher.
                for (var i = 1; i < views.Count; i++)
                {
                    Assert.True(views[i].Rotation.Angle > views[i - 1].Rotation.Angle);
                    Assert.True(Canvas.GetTop(views[i]) < Canvas.GetTop(views[i - 1]));
                }

                // Hit-testing the centre of each card finds that card (despite overlap and rotation).
                foreach (var v in views.Take(8))
                {
                    var centre = v.TranslatePoint(new Point(v.PivotX, v.ItemHeight / 2), canvas);
                    var hit = canvas.InputHitTest(centre) as DependencyObject;
                    var owner = hit;
                    while (owner is not null and not FanItemView) owner = VisualTreeHelper.GetParent(owner);
                    Assert.Same(v, owner);
                }
            }
            finally
            {
                fan.CloseFan(animate: false);
                fan.Close();
            }
        });
    }

    [Fact]
    public void Empty_folder_shows_only_the_folder_item()
    {
        Sta.Run(() =>
        {
            using var thumbs = new ThumbnailService(Path.Combine(Path.GetTempPath(), "dd-fan-test-" + Guid.NewGuid()));
            var fan = new FanWindow(thumbs);
            fan.Open(Anchor, Array.Empty<DownloadEntry>(), 0, @"C:\none", new AppSettings { AnimationSpeed = AnimationSpeed.Off }, dark: true);
            Pump(100);
            var views = ((Canvas)fan.Content).Children.OfType<FanItemView>().ToList();
            Assert.Single(views);
            Assert.Equal("No downloads yet", views[0].ViewModel.Subtitle);
            fan.CloseFan(animate: false);
            Assert.False(fan.IsOpen);
            fan.Close();
        });
    }

    [Fact]
    public void Closing_the_fan_only_hides_it()
    {
        Sta.Run(() =>
        {
            using var thumbs = new ThumbnailService(Path.Combine(Path.GetTempPath(), "dd-fan-test-" + Guid.NewGuid()));
            var fan = new FanWindow(thumbs);
            var closed = 0;
            fan.FanClosed += () => closed++;
            fan.Open(Anchor, Entries(3), 3, @"C:\none", new AppSettings { AnimationSpeed = AnimationSpeed.Normal }, dark: false);
            Pump(500);
            fan.CloseFan();
            Pump(600);
            Assert.Equal(1, closed);
            Assert.False(fan.IsVisible);
            // Re-open works: the window was never destroyed.
            fan.Open(Anchor, Entries(3), 3, @"C:\none", new AppSettings { AnimationSpeed = AnimationSpeed.Off }, dark: false);
            Assert.True(fan.IsVisible);
            fan.CloseFan(animate: false);
            fan.Close();
        });
    }

    [Fact]
    public void Thumbnail_resolution_tracks_dpi()
    {
        Assert.True(FanWindow.ThumbnailPixelsFor(96, 2.0) >= 192);
        Assert.Equal(0, FanWindow.ThumbnailPixelsFor(96, 1.0) % 16);
    }

    [Fact]
    public void View_model_fits_preview_aspect_into_the_box()
    {
        Sta.Run(() =>
        {
            var vm = new FanItemViewModel(100);
            vm.SetThumbnail(new System.Windows.Media.Imaging.WriteableBitmap(200, 100, 96, 96, PixelFormats.Bgra32, null), isIcon: false);
            Assert.Equal(100, vm.ThumbWidth, 1);
            Assert.Equal(50, vm.ThumbHeight, 1);
            Assert.Equal("report", FanItemViewModel.FormatName("report.pdf", showExtension: false));
            Assert.Equal("report.pdf", FanItemViewModel.FormatName("report.pdf", showExtension: true));
        });
    }

    [Fact]
    public void Tray_glyph_is_rendered_at_the_requested_size()
    {
        Sta.Run(() =>
        {
            var light = TrayIconRenderer.Render(24, darkTaskbar: true);
            Assert.Equal(24, light.PixelWidth);
            var icon = BitmapInterop.CreateHIcon(light);
            Assert.NotEqual(IntPtr.Zero, icon);
            NativeMethods.DestroyIcon(icon);
        });
    }

    [Fact]
    public void Downloads_known_folder_resolves()
    {
        var path = DownloadDock.Services.KnownFolders.GetDownloadsFolder();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void Anchor_falls_back_when_the_icon_position_is_unknown()
    {
        var a = FanAnchorResolver.Resolve(null, new POINT(500, 500));
        Assert.False(a.IsExact);
        Assert.True(a.DpiScale >= 1);
        Assert.InRange(a.AnchorXPx, a.WorkArea.Left, a.WorkArea.Right);
    }
}

/// <summary>Writes sample downloads for the CI snapshot step (only when DD_SAMPLE_OUT is set).</summary>
public class SampleGenerator
{
    [Fact]
    public void Write_sample_downloads_for_snapshots()
    {
        var target = Environment.GetEnvironmentVariable("DD_SAMPLE_OUT");
        if (string.IsNullOrEmpty(target)) return;
        Sta.Run(() => SampleFiles.PopulateDownloads(target));
        Assert.True(Directory.GetFiles(target).Length >= 9);
    }
}
