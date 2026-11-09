using System.Diagnostics;
using DownloadDock.Core.Downloads;

namespace DownloadDock.Core.Tests;

public class FolderScannerTests
{
    [Fact]
    public void Returns_newest_first_and_skips_partial_downloads()
    {
        using var f = new TestFolder();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        f.File("old.pdf", writeUtc: t0, createUtc: t0);
        f.File("mid.png", writeUtc: t0.AddHours(1), createUtc: t0.AddHours(1));
        f.File("new.docx", writeUtc: t0.AddHours(2), createUtc: t0.AddHours(2));
        f.File("inprogress.zip.crdownload", writeUtc: t0.AddHours(3), createUtc: t0.AddHours(3));
        Directory.CreateDirectory(Path.Combine(f.Path, "SomeFolder"));

        var result = FolderScanner.Scan(f.Path, 10, includeSubfolders: false);

        Assert.Equal(new[] { "new.docx", "mid.png", "old.pdf" }, result.Newest.Select(e => e.Name));
        Assert.Equal(3, result.TotalVisibleCount);
    }

    [Fact]
    public void Uses_the_later_of_creation_and_write_time_as_date_added()
    {
        // A file copied into Downloads keeps its old write time but gets a new creation time.
        var e = new DownloadEntry("x", "x", 1, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(e.CreationTimeUtc, e.AddedUtc);
        var e2 = e with { LastWriteTimeUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        Assert.Equal(e2.LastWriteTimeUtc, e2.AddedUtc);
    }

    [Fact]
    public void Limits_result_count_but_reports_total()
    {
        using var f = new TestFolder();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 30; i++)
            f.File($"file{i:00}.txt", writeUtc: t0.AddMinutes(i), createUtc: t0.AddMinutes(i));

        var result = FolderScanner.Scan(f.Path, 5, false);

        Assert.Equal(5, result.Newest.Count);
        Assert.Equal(30, result.TotalVisibleCount);
        Assert.Equal(new[] { "file29.txt", "file28.txt", "file27.txt", "file26.txt", "file25.txt" }, result.Newest.Select(e => e.Name));
    }

    [Fact]
    public void Includes_subfolders_only_when_asked()
    {
        using var f = new TestFolder();
        f.File("top.txt");
        f.File(Path.Combine("sub", "nested.txt"));

        Assert.Single(FolderScanner.Scan(f.Path, 10, false).Newest);
        Assert.Equal(2, FolderScanner.Scan(f.Path, 10, true).Newest.Count);
    }

    [Fact]
    public void Missing_folder_returns_empty()
    {
        var result = FolderScanner.Scan(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()), 10, false);
        Assert.Empty(result.Newest);
        Assert.Equal(0, result.TotalVisibleCount);
    }

    [Fact]
    public void Scanning_thousands_of_files_is_fast()
    {
        using var f = new TestFolder();
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5000; i++)
        {
            var p = Path.Combine(f.Path, $"download-{i:00000}.bin");
            File.WriteAllBytes(p, Array.Empty<byte>());
            File.SetLastWriteTimeUtc(p, t0.AddSeconds(i));
            File.SetCreationTimeUtc(p, t0.AddSeconds(i));
        }

        FolderScanner.Scan(f.Path, 200, false); // warm up
        var sw = Stopwatch.StartNew();
        var result = FolderScanner.Scan(f.Path, 200, false);
        sw.Stop();

        Assert.Equal(5000, result.TotalVisibleCount);
        Assert.Equal(200, result.Newest.Count);
        Assert.Equal("download-04999.bin", result.Newest[0].Name);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Scan took {sw.ElapsedMilliseconds} ms");
    }
}
