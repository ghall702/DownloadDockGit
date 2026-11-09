using DownloadDock.Core.Settings;
using DownloadDock.Core.Util;

namespace DownloadDock.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips_all_settings()
    {
        using var f = new TestFolder();
        var store = new SettingsStore(Path.Combine(f.Path, "sub", "settings.json"));
        var s = new AppSettings
        {
            DownloadsFolder = @"D:\Downloads",
            VisibleFiles = 12,
            ThumbnailSize = 120,
            AnimationSpeed = AnimationSpeed.Fast,
            Theme = ThemePreference.Dark,
            StartWithWindows = false,
            ShowFileExtensions = false,
            IncludeSubfolders = true,
            CloseAfterDrop = false,
        };
        store.Save(s);
        Assert.Equal(s, store.Load());
        Assert.Contains("\"Dark\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var f = new TestFolder();
        var loaded = new SettingsStore(Path.Combine(f.Path, "none.json")).Load();
        Assert.Equal(new AppSettings(), loaded);
        Assert.Equal(9, loaded.VisibleFiles);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_is_backed_up()
    {
        using var f = new TestFolder();
        var path = f.File("settings.json", "{ this is not json");
        var loaded = new SettingsStore(path).Load();
        Assert.Equal(new AppSettings(), loaded);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        using var f = new TestFolder();
        var path = f.File("settings.json", "{ \"VisibleFiles\": 500, \"ThumbnailSize\": 2, \"Theme\": 42, \"DownloadsFolder\": \"   \" }");
        var loaded = new SettingsStore(path).Load();
        Assert.Equal(AppSettings.MaxVisibleFiles, loaded.VisibleFiles);
        Assert.Equal(AppSettings.MinThumbnailSize, loaded.ThumbnailSize);
        Assert.Equal(ThemePreference.System, loaded.Theme);
        Assert.Null(loaded.DownloadsFolder);
    }

    [Theory]
    [InlineData(AnimationSpeed.Off, 0)]
    [InlineData(AnimationSpeed.Normal, 1)]
    public void Animation_factor(AnimationSpeed speed, double expected)
        => Assert.Equal(expected, AppSettings.AnimationFactor(speed));
}

public class LruCacheTests
{
    [Fact]
    public void Evicts_least_recently_used()
    {
        var c = new LruCache<string, int>(2);
        c.Set("a", 1);
        c.Set("b", 2);
        Assert.True(c.TryGet("a", out _)); // a is now most recent
        c.Set("c", 3);
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out var a) && a == 1);
        Assert.True(c.TryGet("c", out var cc) && cc == 3);
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Overwrite_and_remove()
    {
        var c = new LruCache<string, int>(3, StringComparer.OrdinalIgnoreCase);
        c.Set("Key", 1);
        c.Set("KEY", 2);
        Assert.Equal(1, c.Count);
        Assert.True(c.TryGet("key", out var v) && v == 2);
        Assert.True(c.Remove("kEy"));
        Assert.Equal(0, c.Count);
    }
}

public class DebouncerTests
{
    [Fact]
    public void Coalesces_a_burst_into_one_call()
    {
        var calls = 0;
        using var d = new Debouncer(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 20; i++) { d.Trigger(); Thread.Sleep(5); }
        // Poll (the thread pool may be busy with other tests), then make sure no second call follows.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref calls) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Thread.Sleep(400);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Max_delay_fires_during_a_continuous_burst()
    {
        var calls = 0;
        using var d = new Debouncer(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(250));
        var end = DateTime.UtcNow.AddMilliseconds(900);
        while (DateTime.UtcNow < end) { d.Trigger(); Thread.Sleep(20); }
        Assert.True(Volatile.Read(ref calls) >= 2);
    }
}
