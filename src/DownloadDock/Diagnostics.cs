using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Fan;
using DownloadDock.Core.Settings;
using DownloadDock.Fan;
using DownloadDock.Interop;
using DownloadDock.Thumbnails;

namespace DownloadDock;

/// <summary>
/// Headless command-line modes for troubleshooting and CI:
///   DownloadDock.exe --thumbnail &lt;file&gt; &lt;out.png&gt; [pixels]
///   DownloadDock.exe --fan-snapshot &lt;folder&gt; &lt;out.png&gt; [light|dark] [count] [thumbSize]
/// Each writes a JSON report next to the PNG.
/// </summary>
internal static class Diagnostics
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--thumbnail" when args.Length >= 3:
                    exitCode = RenderThumbnail(args[1], args[2], args.Length > 3 && int.TryParse(args[3], out var px) ? px : 256);
                    return true;
                case "--fan-snapshot" when args.Length >= 3:
                    exitCode = RenderFanSnapshot(args[1], args[2],
                        dark: args.Length > 3 && args[3].Equals("dark", StringComparison.OrdinalIgnoreCase),
                        count: args.Length > 4 && int.TryParse(args[4], out var c) ? c : 9,
                        thumbSize: args.Length > 5 && int.TryParse(args[5], out var s) ? s : 96);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(args.Length > 2 ? args[2] + ".error.txt" : "downloaddock-error.txt", ex.ToString()); } catch (IOException) { }
            exitCode = 2;
            return true;
        }
    }

    private static int RenderThumbnail(string input, string output, int px)
    {
        var started = DateTime.UtcNow;
        var result = ThumbnailRenderer.Render(Path.GetFullPath(input), px);
        if (result is null) return 1;
        SavePng(result.Image, output);
        WriteReport(output, new
        {
            input,
            source = result.Source,
            isIcon = result.IsIcon,
            width = result.Image.PixelWidth,
            height = result.Image.PixelHeight,
            milliseconds = (DateTime.UtcNow - started).TotalMilliseconds,
        });
        return 0;
    }

    private static int RenderFanSnapshot(string folder, string output, bool dark, int count, int thumbSize)
    {
        var settings = new AppSettings
        {
            VisibleFiles = count,
            ThumbnailSize = thumbSize,
            AnimationSpeed = AnimationSpeed.Off,
            Theme = dark ? ThemePreference.Dark : ThemePreference.Light,
        }.Normalize();
        var scan = FolderScanner.Scan(folder, 200, includeSubfolders: false);

        // A synthetic 1920×1080 monitor with a 48 px taskbar; the tray icon sits where the
        // reference design puts it (left of Wi-Fi / volume / battery / clock).
        var monitor = new RECT(0, 0, 1920, 1080);
        var work = new RECT(0, 0, 1920, 1032);
        var anchor = new FanAnchor(monitor, work, 1.0, 1920 - 330, 1032, FanDirection.Up, true);

        using var thumbs = new ThumbnailService(Path.Combine(Path.GetTempPath(), "DownloadDock-snapshot-cache-" + Environment.ProcessId));
        var px = FanWindow.ThumbnailPixelsFor(settings.ThumbnailSize, 1);
        var sources = new List<object>();
        var tasks = scan.Newest.Take(settings.VisibleFiles).Select(e => thumbs.GetAsync(e, px)).ToArray();
        Wait(Task.WhenAll(tasks), TimeSpan.FromSeconds(90));
        foreach (var (task, entry) in tasks.Zip(scan.Newest))
            sources.Add(new { file = entry.Name, source = task.IsCompletedSuccessfully ? task.Result?.Source : "failed", isIcon = task.IsCompletedSuccessfully && (task.Result?.IsIcon ?? true) });
        Wait(thumbs.GetIconAsync(folder, px), TimeSpan.FromSeconds(10));

        // Capture WPF data-binding errors so the report shows any broken bindings.
        var bindingLog = new StringWriter();
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(bindingLog));
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;

        var fan = new FanWindow(thumbs);
        fan.Open(anchor, scan.Newest, scan.TotalVisibleCount, folder, settings, dark);
        Pump(TimeSpan.FromMilliseconds(600));

        var root = (FrameworkElement)fan.Content;
        var w = (int)Math.Ceiling(fan.Width);
        var h = (int)Math.Ceiling(fan.Height);

        // Render the fan's own visual tree 1:1, then composite it over a wallpaper-like background
        // plus a taskbar strip for context.
        root.UpdateLayout();
        var fanBitmap = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        fanBitmap.Render(root);
        var origin = root.PointToScreen(new Point(0, 0));
        var taskbarTop = anchor.AnchorEdgeYDip - origin.Y;
        var totalH = (int)Math.Max(h, taskbarTop + 48);
        var screenshot = new DrawingVisual();
        using (var dc = screenshot.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x1E, 0x4F, 0xC7), Color.FromRgb(0x7F, 0xA8, 0xE8), 45);
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, totalH));
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x22, 0x33) : Color.FromRgb(0xEE, 0xF1, 0xF6)), null, new Rect(0, taskbarTop, w, 48));
            var iconX = anchor.AnchorXDip - origin.X;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), null, new Rect(iconX - 20, taskbarTop + 4, 40, 40), 4, 4);
            var glyph = Tray.TrayIconRenderer.Render(24, dark);
            dc.DrawImage(glyph, new Rect(iconX - 12, taskbarTop + 12, 24, 24));
            dc.DrawImage(fanBitmap, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, totalH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(screenshot);
        SavePng(rtb, output);

        var views = ((System.Windows.Controls.Canvas)root).Children.OfType<FanItemView>()
            .OrderBy(v => v.SlotIndex)
            .Select(v => new
            {
                slot = v.SlotIndex,
                name = v.ViewModel.DisplayName,
                left = System.Windows.Controls.Canvas.GetLeft(v),
                top = System.Windows.Controls.Canvas.GetTop(v),
                angle = v.Rotation.Angle,
                scale = v.OpenScale.ScaleX,
                opacity = v.Opacity,
                actualWidth = v.ActualWidth,
                actualHeight = v.ActualHeight,
                thumbWidth = v.ViewModel.ThumbWidth,
                thumbHeight = v.ViewModel.ThumbHeight,
                isIcon = v.ViewModel.IsIcon,
                hasThumbnail = v.ViewModel.HasThumbnail,
                labelText = v.NameText.Text,
                labelWidth = v.LabelPill.ActualWidth,
                cardWidth = v.Card.ActualWidth,
                cardHeight = v.Card.ActualHeight,
                dataContextIsViewModel = v.DataContext is ViewModels.FanItemViewModel,
            })
            .ToList();

        WriteReport(output, new
        {
            folder,
            files = scan.Newest.Count,
            total = scan.TotalVisibleCount,
            window = new { width = fan.Width, height = fan.Height, actualWidth = root.ActualWidth, actualHeight = root.ActualHeight, screenX = origin.X, screenY = origin.Y },
            views,
            bindingErrors = bindingLog.ToString(),
            thumbnails = sources,
        });
        fan.CloseFan(animate: false);
        fan.Close();
        return 0;
    }

    private static void Wait(Task task, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = timeout };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        // Continuations marshalled to the UI thread need the dispatcher running.
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Pump(TimeSpan.FromMilliseconds(100)); // let ContinueWith(FromCurrentSynchronizationContext) callbacks run
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SavePng(BitmapSource image, string output)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(output);
        encoder.Save(fs);
    }

    private static void WriteReport(string output, object report)
        => File.WriteAllText(output + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
