using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Settings;
using DownloadDock.Core.Tests;
using DownloadDock.Dragging;
using DownloadDock.Fan;
using DownloadDock.Interop;
using DownloadDock.Thumbnails;

namespace DownloadDock.Windows.Tests;

/// <summary>Dropping files (e.g. a screenshot) onto the Downloads fan, with real OLE drags.</summary>
[Collection("Desktop")]
public class DropOnFanTests
{
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Pump(50);
        }
        return condition();
    }

    /// <summary>Runs the injected drag on another thread while this (fan) thread keeps pumping, as the drop target must.</summary>
    private static DragDriver.Outcome DragWhilePumping(string file, Native.POINT target)
    {
        var task = Task.Run(() => DragDriver.Drag(file, target));
        Assert.True(PumpUntil(() => task.IsCompleted, 40000), "drag did not finish");
        return task.Result;
    }

    /// <summary>Opens a real fan on the primary screen for a temp "Downloads" folder.</summary>
    private static (FanWindow Fan, ThumbnailService Thumbs) OpenFan(string downloads, bool forDrop)
    {
        var thumbs = new ThumbnailService(Path.Combine(Path.GetTempPath(), "dd-drop-cache-" + Guid.NewGuid()));
        var fan = new FanWindow(thumbs);
        var anchor = FanAnchorResolver.Resolve(null, new POINT(760, 700));
        var scan = FolderScanner.Scan(downloads, 50, false);
        fan.Open(anchor, scan.Newest, scan.TotalVisibleCount, downloads, new AppSettings { AnimationSpeed = AnimationSpeed.Off, VisibleFiles = 4 }, dark: false, forDrop: forDrop);
        Pump(500);
        return (fan, thumbs);
    }

    private static Native.POINT FolderItemCentre(FanWindow fan)
    {
        var view = ((Canvas)fan.Content).Children.OfType<FanItemView>().Single(v => v.ViewModel.IsFolderItem);
        var p = view.PointToScreen(new Point(view.PivotX, view.ItemHeight / 2));
        return new Native.POINT { X = (int)p.X, Y = (int)p.Y };
    }

    [SkippableFact]
    public void Dropping_a_file_on_the_open_fan_delivers_it_for_copying()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        var downloads = Directory.CreateDirectory(Path.Combine(f.Path, "Downloads")).FullName;
        f.File(Path.Combine("Downloads", "existing.pdf"), "%PDF");
        var shot = f.File("Screenshot 2026-11-09 09-07-00.png", "png-bytes");

        string[]? dropped = null;
        DragDriver.Outcome? outcome = null;
        Sta.Run(() =>
        {
            var (fan, thumbs) = OpenFan(downloads, forDrop: false);
            try
            {
                fan.FilesDropped += files => dropped = files;
                var target = FolderItemCentre(fan);
                outcome = DragWhilePumping(shot, target);
                Assert.True(PumpUntil(() => dropped is not null, 5000), $"fan never received the drop ({outcome?.Result})");
            }
            finally
            {
                fan.CloseFan(animate: false);
                fan.Close();
                thumbs.Dispose();
            }
        });
        Assert.Equal(new[] { Path.GetFullPath(shot) }, dropped!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(FileDragResult.Dropped, outcome!.Result);
        Assert.Equal(1, outcome.Effect); // copy – the screenshot stays where it was
        Assert.True(File.Exists(shot));
    }

    [SkippableFact]
    public void Sprung_open_fan_does_not_take_focus_accepts_the_drop_and_closes_itself()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        var downloads = Directory.CreateDirectory(Path.Combine(f.Path, "Downloads")).FullName;
        var shot = f.File("shot.png", "png-bytes");

        string[]? dropped = null;
        Sta.Run(() =>
        {
            var before = Native.GetForegroundWindowPublic();
            var (fan, thumbs) = OpenFan(downloads, forDrop: true);
            try
            {
                Assert.True(fan.IsDropMode);
                Assert.Equal(before, Native.GetForegroundWindowPublic());
                fan.FilesDropped += files => dropped = files;
                DragWhilePumping(shot, FolderItemCentre(fan));
                Assert.True(PumpUntil(() => dropped is not null, 5000), "sprung-open fan did not accept the drop");
                Input.MoveTo(new Native.POINT { X = 50, Y = 50 });
                Assert.True(PumpUntil(() => !fan.IsOpen, 6000), "drop-mode fan should close by itself after the drop");
            }
            finally
            {
                if (fan.IsOpen) fan.CloseFan(animate: false);
                fan.Close();
                thumbs.Dispose();
            }
        });
        Assert.NotNull(dropped);
    }

    [SkippableFact]
    public void Files_already_in_downloads_are_not_dropped_back_in()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        var downloads = Directory.CreateDirectory(Path.Combine(f.Path, "Downloads")).FullName;
        var inside = f.File(Path.Combine("Downloads", "report.pdf"), "%PDF");

        string[]? dropped = null;
        Sta.Run(() =>
        {
            var (fan, thumbs) = OpenFan(downloads, forDrop: false);
            try
            {
                fan.FilesDropped += files => dropped = files;
                var outcome = DragWhilePumping(inside, FolderItemCentre(fan));
                Pump(800);
                Assert.NotEqual(FileDragResult.Dropped, outcome.Result);
            }
            finally
            {
                fan.CloseFan(animate: false);
                fan.Close();
                thumbs.Dispose();
            }
        });
        Assert.Null(dropped);
    }

    [SkippableFact]
    public void Drag_source_reports_pointer_movement_for_spring_loading()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        var file = f.File("moving.png", "x");
        using var target = DropTargetWindow.Start(requestMove: false);
        var points = new List<POINT>();
        var started = 0;
        var ended = new List<FileDragResult>();
        void OnMoved(POINT p) { lock (points) points.Add(p); }
        void OnStarted() => started++;
        void OnEnded(FileDragResult r) => ended.Add(r);
        FileDragSource.DragMoved += OnMoved;
        FileDragSource.DragStarted += OnStarted;
        FileDragSource.DragEnded += OnEnded;
        try
        {
            var result = DragDriver.Drag(file, target.ScreenCenter);
            Assert.True(target.Dropped.Wait(TimeSpan.FromSeconds(10)), "drop never arrived");
            Assert.Equal(FileDragResult.Dropped, result.Result);
        }
        finally
        {
            FileDragSource.DragMoved -= OnMoved;
            FileDragSource.DragStarted -= OnStarted;
            FileDragSource.DragEnded -= OnEnded;
        }
        Assert.Equal(1, started);
        Assert.Equal(new[] { FileDragResult.Dropped }, ended);
        Assert.False(FileDragSource.IsDragInProgress);
        lock (points)
        {
            Assert.True(points.Count > 5, $"only {points.Count} pointer updates");
            Assert.Contains(points, p => Math.Abs(p.X - target.ScreenCenter.X) < 30 && Math.Abs(p.Y - target.ScreenCenter.Y) < 30);
        }
    }
}
