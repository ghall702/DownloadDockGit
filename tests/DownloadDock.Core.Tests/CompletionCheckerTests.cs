using DownloadDock.Core.Downloads;

namespace DownloadDock.Core.Tests;

public class CompletionCheckerTests
{
    private sealed class FakeProbe : IFileProbe
    {
        public HashSet<string> Locked { get; } = new();
        public HashSet<string> Existing { get; } = new();
        public bool IsLockedForWriting(string path) => Locked.Contains(path);
        public bool FileExists(string path) => Existing.Contains(path);
    }

    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DownloadEntry Entry(string path, double ageSeconds, long length = 100)
        => new(path, Path.GetFileName(path), length, Now.AddSeconds(-ageSeconds), Now.AddSeconds(-ageSeconds));

    [Fact]
    public void Old_unlocked_file_is_complete()
    {
        var checker = new CompletionChecker(new FakeProbe(), () => Now);
        Assert.Equal(CompletionChecker.State.Complete, checker.Check(Entry(@"C:\d\a.pdf", 3600)));
    }

    [Fact]
    public void File_being_written_is_pending()
    {
        var probe = new FakeProbe();
        probe.Locked.Add(@"C:\d\a.pdf");
        var checker = new CompletionChecker(probe, () => Now);
        Assert.Equal(CompletionChecker.State.Pending, checker.Check(Entry(@"C:\d\a.pdf", 0.2)));
        Assert.Equal(CompletionChecker.State.Pending, checker.Check(Entry(@"C:\d\a.pdf", 30)));
    }

    [Fact]
    public void Firefox_placeholder_next_to_part_file_is_pending()
    {
        var probe = new FakeProbe();
        probe.Existing.Add(@"C:\d\movie.mp4.part");
        var checker = new CompletionChecker(probe, () => Now);
        Assert.Equal(CompletionChecker.State.Pending, checker.Check(Entry(@"C:\d\movie.mp4", 600, 0)));
    }

    [Fact]
    public void Just_written_empty_file_waits_for_content()
    {
        var checker = new CompletionChecker(new FakeProbe(), () => Now);
        Assert.Equal(CompletionChecker.State.Pending, checker.Check(Entry(@"C:\d\new.txt", 0.1, 0)));
        Assert.Equal(CompletionChecker.State.Complete, checker.Check(Entry(@"C:\d\new.txt", 5, 0)));
    }

    [Fact]
    public void Real_probe_detects_open_writer()
    {
        using var f = new TestFolder();
        var path = f.File("writing.bin");
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            if (OperatingSystem.IsWindows())
                Assert.True(RealFileProbe.Instance.IsLockedForWriting(path));
        }
        Assert.False(RealFileProbe.Instance.IsLockedForWriting(path));
    }
}
