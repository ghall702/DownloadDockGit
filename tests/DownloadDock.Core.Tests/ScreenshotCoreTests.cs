using DownloadDock.Core.Fan;
using DownloadDock.Core.Screenshots;
using DownloadDock.Core.Settings;

namespace DownloadDock.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Win+Shift+3", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x33)]
    [InlineData("win + shift + 4", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x34)]
    [InlineData("Ctrl+Alt+S", HotkeyModifiers.Control | HotkeyModifiers.Alt, 'S')]
    [InlineData("Control+Shift+F12", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x7B)]
    [InlineData("Ctrl+PrintScreen", HotkeyModifiers.Control, 0x2C)]
    [InlineData("F9", HotkeyModifiers.None, 0x78)]
    public void Parses_shortcuts(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(vk, hk.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Win+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Win+Banana")]
    public void Rejects_invalid_text(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Formats_canonically_and_round_trips()
    {
        Assert.True(Hotkey.TryParse("shift+win+3", out var hk));
        Assert.Equal("Win+Shift+3", hk.ToString());
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
        Assert.True(Hotkey.TryParse("alt+ctrl+f5", out var f));
        Assert.Equal("Ctrl+Alt+F5", f.ToString());
    }

    [Theory]
    [InlineData("Win+Shift+S")]
    [InlineData("PrintScreen")]
    [InlineData("Win+PrintScreen")]
    [InlineData("Win+L")]
    [InlineData("A")]
    [InlineData("Shift+A")]
    public void Reserved_or_unsafe_shortcuts_are_refused(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.NotNull(hk.Validate());
    }

    [Theory]
    [InlineData("Win+Shift+3")]
    [InlineData("Win+Shift+4")]
    [InlineData("Ctrl+Shift+4")]
    [InlineData("F9")]
    public void Sensible_shortcuts_are_accepted(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Null(hk.Validate());
    }

    [Fact]
    public void Settings_normalise_shortcuts()
    {
        var s = new AppSettings { FullScreenShortcut = "shift+win+3", RegionShortcut = "nonsense", ScreenshotThumbnailSeconds = 999 }.Normalize();
        Assert.Equal("Win+Shift+3", s.FullScreenShortcut);
        Assert.Equal(AppSettings.DefaultRegionShortcut, s.RegionShortcut);
        Assert.Equal(AppSettings.MaxThumbnailSeconds, s.ScreenshotThumbnailSeconds);
        Assert.Equal("", (new AppSettings { RegionShortcut = "  " }).Normalize().RegionShortcut);
    }
}

public class ScreenshotNamingTests
{
    [Fact]
    public void Uses_the_macOS_style_name()
        => Assert.Equal("Screenshot 2026-11-09 08-30-15", ScreenshotNaming.BaseName(new DateTime(2026, 11, 9, 8, 30, 15)));

    [Fact]
    public void Never_overwrites_existing_files()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("dir", "Screenshot 2026-11-09 08-30-15.png"),
            Path.Combine("dir", "Screenshot 2026-11-09 08-30-15 (2).png"),
        };
        var p = ScreenshotNaming.UniquePath("dir", new DateTime(2026, 11, 9, 8, 30, 15), existing.Contains);
        Assert.Equal(Path.Combine("dir", "Screenshot 2026-11-09 08-30-15 (3).png"), p);
    }

    [Fact]
    public void Real_folder_collision()
    {
        using var f = new TestFolder();
        var t = new DateTime(2026, 1, 2, 3, 4, 5);
        var first = ScreenshotNaming.UniquePath(f.Path, t);
        File.WriteAllText(first, "x");
        Assert.NotEqual(first, ScreenshotNaming.UniquePath(f.Path, t));
    }
}

public class ScreenshotCorrelatorTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Snipping_tool_clipboard_is_a_screenshot_but_other_apps_are_not()
    {
        var c = new ScreenshotCorrelator();
        Assert.True(c.IsScreenshotClipboard("ScreenClippingHost", T0));
        Assert.True(c.IsScreenshotClipboard("SnippingTool", T0));
        Assert.False(c.IsScreenshotClipboard("chrome", T0));
        Assert.False(c.IsScreenshotClipboard(null, T0));
    }

    [Fact]
    public void Print_screen_makes_the_next_clipboard_image_a_screenshot_once()
    {
        var c = new ScreenshotCorrelator();
        c.PrintScreenPressed(T0);
        Assert.True(c.IsScreenshotClipboard(null, T0.AddSeconds(1)));
        Assert.False(c.IsScreenshotClipboard(null, T0.AddSeconds(10)));
        c.ClipboardScreenshotArrived(T0.AddSeconds(1));
        Assert.False(c.IsScreenshotClipboard(null, T0.AddSeconds(2)), "consumed by the first image");
    }

    [Fact]
    public void Clipboard_capture_with_autosaved_file_shows_the_file_only()
    {
        var c = new ScreenshotCorrelator();
        c.ClipboardScreenshotArrived(T0);
        Assert.False(c.ResolvePendingClipboard(T0.AddSeconds(1)));
        Assert.True(c.FileScreenshotArrived(T0.AddSeconds(1.2)));
        Assert.False(c.ResolvePendingClipboard(T0.AddSeconds(5)), "nothing left to show from the clipboard");
    }

    [Fact]
    public void Clipboard_only_capture_is_shown_after_the_wait()
    {
        var c = new ScreenshotCorrelator();
        c.ClipboardScreenshotArrived(T0);
        Assert.False(c.ResolvePendingClipboard(T0.AddSeconds(2)));
        Assert.True(c.ResolvePendingClipboard(T0.AddSeconds(3)));
        Assert.False(c.ResolvePendingClipboard(T0.AddSeconds(4)), "only once");
        // Its file turning up late is a duplicate...
        Assert.False(c.FileScreenshotArrived(T0.AddSeconds(5)));
        // ...but a genuinely new screenshot afterwards is shown.
        Assert.True(c.FileScreenshotArrived(T0.AddSeconds(6)));
    }

    [Fact]
    public void File_only_screenshot_is_shown()
    {
        var c = new ScreenshotCorrelator();
        Assert.True(c.FileScreenshotArrived(T0)); // Win+PrtScn writes the file without a clipboard owner
    }
}

public class ToastStackLayoutTests
{
    private static readonly RectD Work = new(0, 0, 1920, 1032);

    [Fact]
    public void Fits_images_into_the_box_keeping_aspect()
    {
        var (w, h) = ToastStackLayout.FitThumbnail(1920, 1080);
        Assert.Equal(ToastStackLayout.MaxWidth, w, 3);
        Assert.Equal(220 * 1080 / 1920.0, h, 3);
        var (pw, ph) = ToastStackLayout.FitThumbnail(400, 4000); // tall receipt
        Assert.Equal(ToastStackLayout.MaxHeight, ph, 3);
        Assert.Equal(ToastStackLayout.MinSide, pw, 3);
    }

    [Fact]
    public void Newest_is_bottom_right_and_older_stack_upwards()
    {
        var pos = ToastStackLayout.Arrange(Work, new[] { (220.0, 124.0), (220.0, 124.0), (160.0, 160.0) });
        Assert.Equal(1920 - ToastStackLayout.Margin - 220, pos[0].X);
        Assert.Equal(1032 - ToastStackLayout.Margin - 124, pos[0].Y);
        Assert.Equal(pos[0].Y - 12 - 124, pos[1].Y);
        Assert.Equal(1920 - ToastStackLayout.Margin - 160, pos[2].X);
        Assert.All(pos, p => Assert.True(p.Fits));
    }

    [Fact]
    public void Stack_reports_items_that_would_leave_the_screen()
    {
        var pos = ToastStackLayout.Arrange(new RectD(0, 0, 800, 400), Enumerable.Repeat((220.0, 160.0), 4).ToList());
        Assert.True(pos[0].Fits);
        Assert.False(pos[^1].Fits);
    }
}

public class TemporaryScreenshotPolicyTests
{
    [Fact]
    public void Only_old_files_not_in_use_are_deleted()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var files = new[]
        {
            ("old.png", now.AddDays(-2)),
            ("fresh.png", now.AddMinutes(-5)),
            ("dragging-old.png", now.AddDays(-3)),
        };
        var delete = TemporaryScreenshotPolicy.FilesToDelete(files, now, new HashSet<string> { "dragging-old.png" });
        Assert.Equal(new[] { "old.png" }, delete);
    }
}
