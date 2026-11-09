using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DownloadDock.Core.Screenshots;
using DownloadDock.Core.Settings;
using DownloadDock.Core.Tests;
using DownloadDock.Screenshots;

namespace DownloadDock.Windows.Tests;

/// <summary>
/// End-to-end tests for the screenshot feature on a real Windows desktop: GDI capture, the global
/// keyboard hook (Win+Shift+3), region selection with injected mouse input, the floating thumbnail
/// (no focus stealing, timer, hover pause), dragging the thumbnail into File Explorer and Windows
/// screenshot detection through the clipboard and the Screenshots folder.
/// </summary>
[Collection("Desktop")]
public class ScreenshotTests
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

    /// <summary>A solid-coloured window at a known physical position (something recognisable to capture).</summary>
    private static Window ColourWindow(Color color, int x, int y, int w, int h)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
            Background = new SolidColorBrush(color), Left = -32000, Top = -32000, Width = 10, Height = 10, ShowActivated = false,
        };
        var hwnd = new WindowInteropHelper(win).EnsureHandle();
        win.Show();
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x0040);
        Pump(300);
        return win;
    }

    private static AppSettings MakeSettings(string folder, int seconds = 10, ScreenshotSaveMode mode = ScreenshotSaveMode.Permanent)
        => new AppSettings { ScreenshotFolder = folder, ScreenshotThumbnailSeconds = seconds, ScreenshotSaveMode = mode, AnimationSpeed = AnimationSpeed.Fast }.Normalize();

    // ------------------------------------------------------------------ capture

    [Fact]
    public void Gdi_capture_returns_the_real_screen_pixels()
    {
        Sta.Run(() =>
        {
            var win = ColourWindow(Color.FromRgb(0x10, 0xC0, 0x30), 200, 200, 160, 120);
            try
            {
                var shot = ScreenCapture.Capture(new Interop.RECT(220, 220, 300, 280));
                Assert.Equal(80, shot.PixelWidth);
                Assert.Equal(60, shot.PixelHeight);
                var px = Pixels.At(shot, 40, 30);
                Assert.True(px.G > 150 && px.R < 60 && px.B < 90, $"expected the green window, got {px}");
                Assert.NotEmpty(ScreenCapture.Monitors());
            }
            finally { win.Close(); }
        });
    }

    // ------------------------------------------------------------------ keyboard hook

    [SkippableFact]
    public void Win_shift_3_triggers_the_full_screen_shortcut()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        Sta.Run(() =>
        {
            using var hotkeys = new HotkeyService(Dispatcher.CurrentDispatcher);
            Assert.True(hotkeys.IsHooked);
            Assert.True(Hotkey.TryParse("Win+Shift+3", out var hk));
            hotkeys.SetHotkeys(new Dictionary<string, Hotkey> { ["fullscreen"] = hk });
            var fired = new List<string>();
            hotkeys.HotkeyPressed += fired.Add;

            Keys.Chord(0x5B, 0x10, 0x33); // LWin + Shift + 3
            Assert.True(PumpUntil(() => fired.Count > 0, 3000), "shortcut not detected");
            Assert.Equal(new[] { "fullscreen" }, fired);

            // A different digit is not ours and passes straight through.
            Keys.Chord(0x5B, 0x10, 0x37);
            Pump(400);
            Assert.Single(fired);
        });
    }

    [SkippableFact]
    public void Shortcut_recorder_captures_the_next_combination()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        Sta.Run(() =>
        {
            using var hotkeys = new HotkeyService(Dispatcher.CurrentDispatcher);
            Hotkey? recorded = null;
            hotkeys.BeginRecording(h => recorded = h, () => { });
            Keys.Chord(0x11, 0x12, 0x4B); // Ctrl + Alt + K
            Assert.True(PumpUntil(() => recorded is not null, 3000));
            hotkeys.EndRecording();
            Assert.Equal("Ctrl+Alt+K", recorded!.Value.ToString());
        });
    }

    // ------------------------------------------------------------------ region selection

    [SkippableFact]
    public void Dragging_a_rectangle_captures_exactly_that_area()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        Sta.Run(() =>
        {
            var win = ColourWindow(Color.FromRgb(0xD0, 0x20, 0x20), 240, 240, 300, 220);
            try
            {
                var selector = new RegionSelector(null);
                selector.Shown += () =>
                {
                    var t = new Thread(() =>
                    {
                        Thread.Sleep(300);
                        Input.MoveTo(new Native.POINT { X = 300, Y = 300 });
                        Thread.Sleep(80);
                        Input.LeftDown();
                        for (var i = 1; i <= 10; i++) { Input.MoveTo(new Native.POINT { X = 300 + 12 * i, Y = 300 + 9 * i }); Thread.Sleep(25); }
                        Thread.Sleep(80);
                        Input.LeftUp();
                    }) { IsBackground = true };
                    t.Start();
                };
                var task = selector.SelectAsync();
                Assert.True(PumpUntil(() => task.IsCompleted, 10000), "selection never completed");
                var result = task.Result;
                Assert.NotNull(result);
                Assert.InRange(result!.Image.PixelWidth, 118, 122);
                Assert.InRange(result.Image.PixelHeight, 88, 92);
                var px = Pixels.At(result.Image, result.Image.PixelWidth / 2, result.Image.PixelHeight / 2);
                Assert.True(px.R > 170 && px.G < 70, $"expected the red window inside the selection, got {px}");
                Assert.Equal(300, result.ScreenRect.Left);
            }
            finally { win.Close(); }
        });
    }

    [SkippableFact]
    public void Escape_cancels_region_selection()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        Sta.Run(() =>
        {
            using var hotkeys = new HotkeyService(Dispatcher.CurrentDispatcher);
            var selector = new RegionSelector(hotkeys);
            selector.Shown += () => new Thread(() => { Thread.Sleep(300); Keys.Press(0x1B); }) { IsBackground = true }.Start();
            var task = selector.SelectAsync();
            Assert.True(PumpUntil(() => task.IsCompleted, 8000), "Esc did not cancel");
            Assert.Null(task.Result);
            Assert.False(selector.IsActive);
        });
    }

    // ------------------------------------------------------------------ storage

    [Fact]
    public void Screenshots_are_saved_as_png_with_macos_names_and_temporary_files_survive_while_in_use()
    {
        using var f = new TestFolder();
        Sta.Run(() =>
        {
            var settings = MakeSettings(f.Path);
            var store = new ScreenshotStore(() => settings);
            var image = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgr32, null, new byte[4 * 4 * 3], 16);
            var item = store.Save(image, "test", IntPtr.Zero);
            var path = item.WaitForFile(TimeSpan.FromSeconds(10))!;
            Assert.StartsWith(Path.Combine(f.Path, "Screenshot "), path);
            Assert.EndsWith(".png", path);
            Assert.True(store.IsOwnFile(path));
            var decoded = new PngBitmapDecoder(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(4, decoded.PixelWidth);

            // Same second → a different, non-overwriting name.
            var second = store.Save(image, "test", IntPtr.Zero).WaitForFile(TimeSpan.FromSeconds(10))!;
            Assert.NotEqual(path, second);

            // Temporary mode: old files are cleaned up, but never one that is being dragged.
            var tempSettings = MakeSettings(f.Path, mode: ScreenshotSaveMode.Temporary);
            var tempStore = new ScreenshotStore(() => tempSettings);
            var temp = tempStore.Save(image, "test", IntPtr.Zero);
            var tempPath = temp.WaitForFile(TimeSpan.FromSeconds(10))!;
            Assert.StartsWith(ScreenshotStore.TemporaryFolder, tempPath);
            File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow.AddDays(-3));
            using (tempStore.Use(tempPath))
            {
                tempStore.CleanTemporaryFiles();
                Assert.True(File.Exists(tempPath), "a file in use must never be deleted");
            }
            var kept = tempStore.MakePermanent(temp);
            Assert.StartsWith(f.Path, kept);
            Assert.True(File.Exists(kept));
            tempStore.CleanTemporaryFiles();
            Assert.False(File.Exists(tempPath), "old, unused temporary file should be cleaned up");
        });
    }

    // ------------------------------------------------------------------ floating thumbnail

    private static ScreenshotItem SampleItem(string folder, AppSettings settings)
    {
        var path = Sta.Run(() => SampleFiles.Image(folder, "shot.png", 1280, 720));
        var image = new PngBitmapDecoder(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return ScreenshotStore.FromExistingFile(path, image, "test", IntPtr.Zero);
    }

    [SkippableFact]
    public void Thumbnail_appears_bottom_right_without_stealing_focus_and_dismisses_itself()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        Sta.Run(() =>
        {
            var settings = MakeSettings(f.Path, seconds: 3);
            var item = SampleItem(f.Path, settings);
            var focus = ColourWindow(Colors.SteelBlue, 100, 100, 200, 200);
            focus.Activate();
            Pump(300);
            var before = Native.GetForegroundWindowPublic();
            Input.MoveTo(new Native.POINT { X = 150, Y = 150 }); // keep the pointer away from the thumbnail
            using var manager = new ScreenshotToastManager(() => settings, new ScreenshotStore(() => settings), Dispatcher.CurrentDispatcher);
            var closed = false;
            manager.ToastClosed += _ => closed = true;
            var toast = manager.Show(item);
            Pump(400);
            try
            {
                Assert.True(toast.IsVisible);
                Assert.Equal(before, Native.GetForegroundWindowPublic()); // focus stayed where it was
                var mon = toast.Monitor;
                var r = toast.CurrentRect;
                Assert.True(mon.WorkArea.Right - r.Right < 60 * mon.DpiScale, $"not at the right edge: {r} in {mon.WorkArea}");
                Assert.True(mon.WorkArea.Bottom - r.Bottom < 60 * mon.DpiScale, $"not above the taskbar: {r} in {mon.WorkArea}");
                Assert.True(r.Bottom <= mon.WorkArea.Bottom + 1, "must not cover the taskbar");
                Assert.True(PumpUntil(() => closed, 6000), "thumbnail did not dismiss itself");
            }
            finally { focus.Close(); }
        });
    }

    [SkippableFact]
    public void Hovering_pauses_the_dismissal_timer()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        Sta.Run(() =>
        {
            var settings = MakeSettings(f.Path, seconds: 3);
            var item = SampleItem(f.Path, settings);
            using var manager = new ScreenshotToastManager(() => settings, new ScreenshotStore(() => settings), Dispatcher.CurrentDispatcher);
            var closed = false;
            manager.ToastClosed += _ => closed = true;
            var toast = manager.Show(item);
            Pump(600);
            var r = toast.CurrentRect;
            Input.MoveTo(new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 });
            Pump(4500); // longer than the 3 s timeout
            Assert.False(closed, "thumbnail closed while hovered");
            Input.MoveTo(new Native.POINT { X = 100, Y = 100 });
            Assert.True(PumpUntil(() => closed, 6000), "thumbnail did not resume its countdown after hover");
        });
    }

    [SkippableFact]
    public void Dragging_the_thumbnail_into_file_explorer_copies_the_png()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        var destination = Directory.CreateDirectory(Path.Combine(f.Path, "Shot Target " + Guid.NewGuid().ToString("N")[..6])).FullName;
        var explorer = ExplorerWindow.Open(destination);
        Skip.If(explorer == IntPtr.Zero, "File Explorer window could not be opened");
        try
        {
            Sta.Run(() =>
            {
                var settings = MakeSettings(f.Path, seconds: 30);
                var item = SampleItem(f.Path, settings);
                var source = item.FileTask.Result;
                var store = new ScreenshotStore(() => settings);
                using var manager = new ScreenshotToastManager(() => settings, store, Dispatcher.CurrentDispatcher);
                var closed = false;
                manager.ToastClosed += _ => closed = true;
                var toast = manager.Show(item);
                Pump(700);
                var r = toast.CurrentRect;
                // Card centre (the window includes a shadow margin on every side).
                var start = new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
                var target = ExplorerWindow.ListCentre(explorer);

                var mover = new Thread(() =>
                {
                    Input.MoveTo(start);
                    Thread.Sleep(150);
                    Input.LeftDown();
                    Thread.Sleep(100);
                    // Move up-left first (a rightward move would be the swipe-to-dismiss gesture).
                    for (var i = 1; i <= 30; i++)
                    {
                        Input.MoveTo(new Native.POINT { X = start.X + (target.X - start.X) * i / 30, Y = start.Y + (target.Y - start.Y) * i / 30 });
                        Thread.Sleep(25);
                    }
                    for (var i = 0; i < 4; i++) { Input.MoveTo(new Native.POINT { X = target.X + (i % 2 == 0 ? 2 : -2), Y = target.Y }); Thread.Sleep(60); }
                    Input.LeftUp();
                }) { IsBackground = true };
                mover.Start();

                var copied = Path.Combine(destination, Path.GetFileName(source));
                Assert.True(PumpUntil(() => File.Exists(copied), 20000), "Explorer did not receive the screenshot");
                Assert.True(File.Exists(source), "the original screenshot must remain");
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copied));
                Assert.True(PumpUntil(() => closed, 3000), "thumbnail should go away after a successful drop");
                Assert.False(store.IsInUse(source), "drag lease must be released");
            });
        }
        finally
        {
            ExplorerWindow.Close(explorer);
        }
    }

    // ------------------------------------------------------------------ Windows screenshot detection

    [Fact]
    public void Clipboard_image_after_print_screen_is_detected_and_saved()
    {
        using var f = new TestFolder();
        Sta.Run(() =>
        {
            var settings = MakeSettings(f.Path);
            var store = new ScreenshotStore(() => settings);
            using var detector = new WindowsScreenshotDetector(Dispatcher.CurrentDispatcher, store);
            var watched = Directory.CreateDirectory(Path.Combine(f.Path, "watched")).FullName;
            detector.Start(watched);
            var detected = new List<ScreenshotItem>();
            detector.ScreenshotDetected += detected.Add;

            // An ordinary image copy (no Print Screen, not from a capture tool) is ignored.
            Clipboard.SetImage(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr32, null, new byte[8 * 8 * 4], 32));
            Pump(3500);
            Assert.Empty(detected);

            // Print Screen followed by a clipboard bitmap = a screenshot.
            detector.OnPrintScreen();
            var pixels = Enumerable.Repeat((byte)0x80, 20 * 10 * 4).ToArray();
            Clipboard.SetImage(BitmapSource.Create(20, 10, 96, 96, PixelFormats.Bgr32, null, pixels, 80));
            Assert.True(PumpUntil(() => detected.Count == 1, 6000), "clipboard screenshot not detected");
            var path = detected[0].WaitForFile(TimeSpan.FromSeconds(10));
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal(20, detected[0].Image.PixelWidth);
        });
    }

    [Fact]
    public void New_file_in_the_screenshots_folder_is_detected()
    {
        using var f = new TestFolder();
        Sta.Run(() =>
        {
            var settings = MakeSettings(f.Path);
            var store = new ScreenshotStore(() => settings);
            using var detector = new WindowsScreenshotDetector(Dispatcher.CurrentDispatcher, store);
            var watched = Directory.CreateDirectory(Path.Combine(f.Path, "Screenshots")).FullName;
            detector.Start(watched);
            var detected = new List<ScreenshotItem>();
            detector.ScreenshotDetected += detected.Add;

            var path = SampleFiles.Image(watched, "Screenshot 2026-11-09 083015.png", 300, 200);
            Assert.True(PumpUntil(() => detected.Count == 1, 8000), "Snipping Tool-style file not detected");
            Assert.Equal(path, detected[0].FileTask.Result);

            // Files DownloadDock wrote itself are not shown twice.
            var ownSettings = MakeSettings(watched);
            var ownStore = new ScreenshotStore(() => ownSettings);
            using var ownDetector = new WindowsScreenshotDetector(Dispatcher.CurrentDispatcher, ownStore);
            ownDetector.Start(watched);
            var ownDetected = new List<ScreenshotItem>();
            ownDetector.ScreenshotDetected += ownDetected.Add;
            ownStore.Save(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, new byte[64], 16), "own", IntPtr.Zero).WaitForFile(TimeSpan.FromSeconds(5));
            Pump(2500);
            Assert.Empty(ownDetected);
        });
    }

    // ------------------------------------------------------------------ editor

    [Fact]
    public void Editor_burns_markup_into_a_full_resolution_image()
    {
        using var f = new TestFolder();
        Sta.Run(() =>
        {
            var settings = MakeSettings(f.Path);
            var item = SampleItem(f.Path, settings);
            var editor = new ScreenshotEditorWindow(item, new ScreenshotStore(() => settings), ThemePreference.Light);
            var ink = (System.Windows.Controls.InkCanvas)editor.FindName("Ink");
            var points = new System.Windows.Input.StylusPointCollection { new System.Windows.Input.StylusPoint(100, 100), new System.Windows.Input.StylusPoint(400, 100) };
            ink.Strokes.Add(new System.Windows.Ink.Stroke(points) { DrawingAttributes = new System.Windows.Ink.DrawingAttributes { Color = Colors.Magenta, Width = 12, Height = 12 } });
            var flat = editor.Flatten();
            Assert.Equal(1280, flat.PixelWidth);
            Assert.Equal(720, flat.PixelHeight);
            var px = Pixels.At(flat, 250, 100);
            Assert.True(px.R > 200 && px.B > 200 && px.G < 80, $"markup missing, got {px}");
            editor.Close();
        });
    }
}

/// <summary>Keyboard input injection for the hotkey tests.</summary>
internal static class Keys
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public short wVk, wScan; public int dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public int type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll")] private static extern int SendInput(int n, INPUT[] inputs, int size);

    private static void Send(int vk, bool up)
    {
        var input = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = (short)vk, dwFlags = up ? 2 : 0 } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void Press(int vk) { Send(vk, false); Thread.Sleep(30); Send(vk, true); }

    /// <summary>Presses modifiers then the key, releasing in reverse order.</summary>
    public static void Chord(params int[] keys)
    {
        foreach (var k in keys) { Send(k, false); Thread.Sleep(40); }
        foreach (var k in keys.Reverse()) { Send(k, true); Thread.Sleep(40); }
    }
}
