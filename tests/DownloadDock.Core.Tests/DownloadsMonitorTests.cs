using System.Collections.Concurrent;
using DownloadDock.Core.Downloads;

namespace DownloadDock.Core.Tests;

[Collection("Timing")]
public class DownloadsMonitorTests
{
    private static DownloadsMonitor NewMonitor() => new(TimeSpan.FromMilliseconds(80), new CompletionChecker { SettlePeriod = TimeSpan.Zero })
    {
        PendingRecheckDelay = TimeSpan.FromMilliseconds(200),
    };

    private static DownloadsSnapshot WaitFor(BlockingCollection<DownloadsSnapshot> snaps, Func<DownloadsSnapshot, bool> predicate, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        DownloadsSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (snaps.TryTake(out var s, 100))
            {
                last = s;
                if (predicate(s)) return s;
            }
        }
        throw new TimeoutException("Expected snapshot never arrived. Last: " + string.Join(", ", last?.Items.Select(i => i.Name) ?? Array.Empty<string>()));
    }

    [Fact]
    public void Detects_new_renamed_and_deleted_files()
    {
        using var f = new TestFolder();
        using var monitor = NewMonitor();
        var snaps = new BlockingCollection<DownloadsSnapshot>();
        monitor.SnapshotChanged += (_, s) => snaps.Add(s);
        monitor.Start(f.Path, 20, false);
        WaitFor(snaps, s => s.Items.Count == 0);

        // Chrome-style download: write .crdownload, then rename to the final name.
        var partial = Path.Combine(f.Path, "Invoice.pdf.crdownload");
        File.WriteAllText(partial, "%PDF-1.4 partial");
        Thread.Sleep(300);
        Assert.DoesNotContain(monitor.Current!.Items, i => i.Name.EndsWith(".crdownload"));

        File.Move(partial, Path.Combine(f.Path, "Invoice.pdf"));
        WaitFor(snaps, s => s.Items.Any(i => i.Name == "Invoice.pdf"));

        File.Move(Path.Combine(f.Path, "Invoice.pdf"), Path.Combine(f.Path, "Invoice-2026.pdf"));
        WaitFor(snaps, s => s.Items.Any(i => i.Name == "Invoice-2026.pdf") && s.Items.All(i => i.Name != "Invoice.pdf"));

        File.Delete(Path.Combine(f.Path, "Invoice-2026.pdf"));
        WaitFor(snaps, s => s.Items.Count == 0);
    }

    [Fact]
    public void Newest_download_is_first()
    {
        using var f = new TestFolder();
        var old = DateTime.UtcNow.AddDays(-2);
        f.File("older.png", writeUtc: old, createUtc: old);
        using var monitor = NewMonitor();
        var snaps = new BlockingCollection<DownloadsSnapshot>();
        monitor.SnapshotChanged += (_, s) => snaps.Add(s);
        monitor.Start(f.Path, 20, false);
        WaitFor(snaps, s => s.Items.Count == 1);

        f.File("newer.jpg");
        var snap = WaitFor(snaps, s => s.Items.Count == 2);
        Assert.Equal("newer.jpg", snap.Items[0].Name);
    }

    [Fact]
    public void Missing_folder_is_picked_up_when_created()
    {
        using var f = new TestFolder();
        var target = Path.Combine(f.Path, "later");
        using var monitor = NewMonitor();
        var snaps = new BlockingCollection<DownloadsSnapshot>();
        monitor.SnapshotChanged += (_, s) => snaps.Add(s);
        monitor.Start(target, 20, false);
        var first = WaitFor(snaps, _ => true);
        Assert.False(first.FolderExists);

        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "hello");
        WaitFor(snaps, s => s.FolderExists && s.Items.Count == 1, timeoutMs: 15000);
    }

    [Fact]
    public void Bursts_of_changes_are_debounced()
    {
        using var f = new TestFolder();
        using var monitor = NewMonitor();
        var count = 0;
        monitor.SnapshotChanged += (_, _) => Interlocked.Increment(ref count);
        monitor.Start(f.Path, 200, false);
        Thread.Sleep(400);
        var before = Volatile.Read(ref count);
        for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(f.Path, $"f{i}.txt"), "x");
        Thread.Sleep(1500);
        var notifications = Volatile.Read(ref count) - before;
        Assert.InRange(notifications, 1, 10);
        Assert.Equal(100, monitor.Current!.Items.Count);
    }
}
