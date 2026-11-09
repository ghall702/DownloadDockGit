using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DownloadDock.Core.Tests;
using DownloadDock.Dragging;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace DownloadDock.Windows.Tests;

/// <summary>
/// Verifies the drag source produces genuine file drags: CF_HDROP with the real path, the Shell's
/// IDList format (what Explorer/Office/Chrome read), copy-only effects, and – end to end – that an OLE
/// drop target in another apartment, and File Explorer itself, receive the actual file.
/// </summary>
[Collection("Desktop")]
public class DragDropTests
{
    // ------------------------------------------------------------------ data object

    [Fact]
    public void Shell_data_object_exposes_the_real_file_as_CF_HDROP()
    {
        using var f = new TestFolder();
        var file = f.File("Invoice.pdf", "%PDF-1.4");
        Sta.Run(() =>
        {
            var obj = FileDragSource.CreateShellDataObject(new[] { file });
            Assert.NotNull(obj);
            Assert.Equal(new[] { Path.GetFullPath(file) }, FileDragSource.ReadHDrop(obj!), StringComparer.OrdinalIgnoreCase);
            Assert.True(HasFormat(obj!, "Shell IDList Array"), "Shell IDList Array (used by Explorer and Office) missing");
            Assert.True(HasFormat(obj!, "FileNameW"), "FileNameW missing");
        });
    }

    [Fact]
    public void Multiple_files_are_supported()
    {
        using var f = new TestFolder();
        var a = f.File("a.txt");
        var b = f.File("b.txt");
        Sta.Run(() =>
        {
            var obj = FileDragSource.CreateShellDataObject(new[] { a, b })!;
            Assert.Equal(2, FileDragSource.ReadHDrop(obj).Count);
        });
    }

    [Fact]
    public void Preferred_drop_effect_is_copy()
    {
        using var f = new TestFolder();
        var file = f.File("photo.jpg");
        Sta.Run(() =>
        {
            var obj = FileDragSource.CreateShellDataObject(new[] { file })!;
            FileDragSource.SetPreferredDropEffect(obj, 1);
            Assert.Equal(1, ReadDword(obj, "Preferred DropEffect"));
        });
    }

    [Fact]
    public void Fallback_data_object_also_carries_CF_HDROP()
    {
        using var f = new TestFolder();
        var file = f.File("report.docx");
        Sta.Run(() =>
        {
            var obj = FileDragSource.CreateFallbackDataObject(new[] { file });
            Assert.Equal(new[] { file }, FileDragSource.ReadHDrop(obj));
        });
    }

    [Fact]
    public void Drag_image_is_created_from_a_thumbnail()
    {
        Sta.Run(() =>
        {
            var thumb = new System.Windows.Media.Imaging.WriteableBitmap(120, 160, 96, 96, PixelFormats.Bgra32, null);
            var img = FileDragSource.CreateDragImage(thumb, 96, 1.5, isIcon: false);
            Assert.NotNull(img);
            Assert.True(img!.PixelWidth > 72 && img.PixelHeight > 96 * 1.5);
        });
    }

    // ------------------------------------------------------------------ end to end

    [SkippableFact]
    public void End_to_end_drag_delivers_the_file_with_copy_semantics_and_keeps_the_original()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop (input injection unavailable)");
        using var f = new TestFolder();
        var file = f.File("Beach Photo.jpg", "jpeg-bytes-here");
        var before = File.ReadAllBytes(file);

        using var target = DropTargetWindow.Start(requestMove: false);
        var result = DragDriver.Drag(file, target.ScreenCenter);

        Assert.True(target.Dropped.Wait(TimeSpan.FromSeconds(10)), $"drop never arrived (source result {result.Result}, effect {result.Effect})");
        Assert.Equal(new[] { Path.GetFullPath(file) }, target.Files, StringComparer.OrdinalIgnoreCase);
        Assert.True(target.AllowedEffects.HasFlag(DragDropEffects.Copy));
        Assert.False(target.AllowedEffects.HasFlag(DragDropEffects.Move), "the source must never offer MOVE");
        Assert.Equal(FileDragResult.Dropped, result.Result);
        Assert.Equal(1, result.Effect); // DROPEFFECT_COPY
        Assert.True(File.Exists(file));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [SkippableFact]
    public void A_target_that_wants_to_move_cannot_take_the_original()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop (input injection unavailable)");
        using var f = new TestFolder();
        var file = f.File("Invoice.pdf", "%PDF-1.4 original");

        using var target = DropTargetWindow.Start(requestMove: true);
        var result = DragDriver.Drag(file, target.ScreenCenter);

        Assert.True(target.SawDragOver.Wait(TimeSpan.FromSeconds(5)), "drag never reached the target");
        Assert.False(target.Dropped.IsSet, "a move-only drop must be refused");
        Assert.NotEqual(FileDragResult.Dropped, result.Result);
        Assert.True(File.Exists(file));
    }

    [SkippableFact]
    public void Dropping_on_file_explorer_copies_the_file()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop (input injection unavailable)");
        using var f = new TestFolder();
        var source = f.File(Path.Combine("src", "Design Assets.zip"), "zip-bytes");
        var destination = Directory.CreateDirectory(Path.Combine(f.Path, "Explorer Target " + Guid.NewGuid().ToString("N")[..6])).FullName;

        var explorer = ExplorerWindow.Open(destination);
        Skip.If(explorer == IntPtr.Zero, "File Explorer window could not be opened on this machine");
        try
        {
            var centre = ExplorerWindow.ListCentre(explorer);
            var result = DragDriver.Drag(source, centre);
            var copied = Path.Combine(destination, Path.GetFileName(source));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(copied) && DateTime.UtcNow < deadline) Thread.Sleep(200);

            Assert.True(File.Exists(copied), $"Explorer did not receive the file (source result {result.Result}, effect {result.Effect})");
            Assert.True(File.Exists(source), "the original download must remain");
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copied));
        }
        finally
        {
            ExplorerWindow.Close(explorer);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static bool HasFormat(ComTypes.IDataObject obj, string name)
    {
        var fmt = Format(name);
        return obj.QueryGetData(ref fmt) == 0;
    }

    private static int ReadDword(ComTypes.IDataObject obj, string name)
    {
        var fmt = Format(name);
        obj.GetData(ref fmt, out var medium);
        try
        {
            var p = Native.GlobalLock(medium.unionmember);
            try { return Marshal.ReadInt32(p); }
            finally { Native.GlobalUnlock(medium.unionmember); }
        }
        finally
        {
            Native.ReleaseStgMedium(ref medium);
        }
    }

    private static ComTypes.FORMATETC Format(string name) => new()
    {
        cfFormat = unchecked((short)Native.RegisterClipboardFormatW(name)),
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = ComTypes.TYMED.TYMED_HGLOBAL,
    };
}

/// <summary>A WPF drop target running on its own STA thread (a separate COM apartment from the source).</summary>
internal sealed class DropTargetWindow : IDisposable
{
    private Dispatcher? _dispatcher;
    private Window? _window;

    public ManualResetEventSlim Dropped { get; } = new();
    public ManualResetEventSlim SawDragOver { get; } = new();
    public string[] Files { get; private set; } = Array.Empty<string>();
    public DragDropEffects AllowedEffects { get; private set; }
    public Native.POINT ScreenCenter { get; private set; }

    public static DropTargetWindow Start(bool requestMove)
    {
        var t = new DropTargetWindow();
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var w = new Window
            {
                Title = "DownloadDock drop target",
                Left = 700, Top = 200, Width = 320, Height = 320,
                WindowStyle = WindowStyle.None, Topmost = true, ShowInTaskbar = false,
                Background = Brushes.LightGreen, AllowDrop = true,
            };
            void Over(object s, DragEventArgs e)
            {
                t.AllowedEffects = e.AllowedEffects;
                t.SawDragOver.Set();
                e.Effects = requestMove ? DragDropEffects.Move & e.AllowedEffects : DragDropEffects.Copy;
                e.Handled = true;
            }
            w.DragEnter += Over;
            w.DragOver += Over;
            w.Drop += (_, e) =>
            {
                t.Files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
                e.Effects = requestMove ? DragDropEffects.Move & e.AllowedEffects : DragDropEffects.Copy;
                e.Handled = true;
                t.Dropped.Set();
            };
            w.Show();
            w.Activate();
            var hwnd = new WindowInteropHelper(w).Handle;
            Native.GetWindowRect(hwnd, out var r);
            t.ScreenCenter = new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
            t._window = w;
            t._dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("drop target did not start");
        return t;
    }

    public void Dispose()
    {
        _dispatcher?.Invoke(() => _window?.Close());
        _dispatcher?.InvokeShutdown();
    }
}

/// <summary>Starts a real drag from a source window and drives the mouse with SendInput.</summary>
internal static class DragDriver
{
    public sealed record Outcome(FileDragResult Result, int Effect);

    public static Outcome Drag(string file, Native.POINT target)
    {
        Outcome outcome = new(FileDragResult.Failed, 0);
        Sta.Run(() =>
        {
            var source = new Window
            {
                Left = 150, Top = 200, Width = 220, Height = 220,
                WindowStyle = WindowStyle.None, Topmost = true, ShowInTaskbar = false, Background = Brushes.SteelBlue,
            };
            source.Show();
            source.Activate();
            var hwnd = new WindowInteropHelper(source).Handle;
            Native.GetWindowRect(hwnd, out var r);
            var start = new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
            Pump(200);

            // Press the button on the source, then let a helper thread move and release while
            // OLE's modal drag loop runs on this thread.
            Input.MoveTo(start);
            Pump(80);
            Input.LeftDown();
            Pump(80);
            var mover = new Thread(() =>
            {
                Thread.Sleep(250);
                const int steps = 30;
                for (var i = 1; i <= steps; i++)
                {
                    Input.MoveTo(new Native.POINT
                    {
                        X = start.X + (target.X - start.X) * i / steps,
                        Y = start.Y + (target.Y - start.Y) * i / steps,
                    });
                    Thread.Sleep(25);
                }
                // A couple of wiggles at the destination so the target sees DragOver.
                for (var i = 0; i < 4; i++) { Input.MoveTo(new Native.POINT { X = target.X + (i % 2 == 0 ? 2 : -2), Y = target.Y }); Thread.Sleep(60); }
                Input.LeftUp();
            }) { IsBackground = true };
            mover.Start();

            var thumb = new System.Windows.Media.Imaging.WriteableBitmap(80, 100, 96, 96, PixelFormats.Bgra32, null);
            var image = FileDragSource.CreateDragImage(thumb, 80, 1, false);
            var result = FileDragSource.DoDragDrop(hwnd, new[] { file }, image, new Point(10, 10), out var effect);
            mover.Join(5000);
            outcome = new Outcome(result, effect);
            source.Close();
        }, timeoutMs: 60_000);
        return outcome;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

internal static class ExplorerWindow
{
    public static IntPtr Open(string folder)
    {
        var before = Native.FindExplorerWindows();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
        var name = Path.GetFileName(folder);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var h in Native.FindExplorerWindows())
            {
                if (before.Contains(h)) continue;
                if (Native.GetTitle(h).Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    Native.SetWindowPos(h, IntPtr.Zero, 640, 120, 760, 560, 0x0040);
                    Native.SetForegroundWindow(h);
                    Thread.Sleep(1500); // let the view finish loading
                    return h;
                }
            }
            Thread.Sleep(250);
        }
        return IntPtr.Zero;
    }

    public static Native.POINT ListCentre(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var r);
        // Right of the navigation pane, below the command bar: the empty file-list area.
        return new Native.POINT { X = r.Left + (r.Right - r.Left) * 2 / 3, Y = r.Top + (r.Bottom - r.Top) * 2 / 3 };
    }

    public static void Close(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) Native.PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }
}

internal static class Input
{
    public static bool HasInteractiveDesktop()
    {
        if (!Environment.UserInteractive) return false;
        if (!Native.GetCursorPos(out var p)) return false;
        // If the cursor can't be moved, input injection won't work either.
        var probe = new Native.POINT { X = p.X == 300 ? 301 : 300, Y = 300 };
        if (!Native.SetCursorPos(probe.X, probe.Y)) return false;
        Native.GetCursorPos(out var after);
        return after.X == probe.X && after.Y == probe.Y;
    }

    public static void MoveTo(Native.POINT p)
    {
        // Absolute coordinates are normalised to 0..65535 over the virtual desktop.
        var vx = Native.GetSystemMetrics(76); var vy = Native.GetSystemMetrics(77);
        var vw = Native.GetSystemMetrics(78); var vh = Native.GetSystemMetrics(79);
        var nx = (int)Math.Round((p.X - vx) * 65535.0 / Math.Max(1, vw - 1));
        var ny = (int)Math.Round((p.Y - vy) * 65535.0 / Math.Max(1, vh - 1));
        Send(0x0001 | 0x8000 | 0x4000, nx, ny); // MOVE | ABSOLUTE | VIRTUALDESK
    }

    public static void LeftDown() => Send(0x0002, 0, 0);

    public static void LeftUp() => Send(0x0004, 0, 0);

    private static void Send(int flags, int dx, int dy)
    {
        var input = new Native.INPUT
        {
            type = 0,
            mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags },
        };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi; // 8-byte aligned union on x64
    }

    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] public static extern IntPtr GetForegroundWindowPublic();
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, char[] buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, char[] buf, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("ole32.dll")] public static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM m);

    public delegate bool EnumProc(IntPtr h, IntPtr l);

    public static string GetTitle(IntPtr h)
    {
        var buf = new char[512];
        var n = GetWindowTextW(h, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    public static HashSet<IntPtr> FindExplorerWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            var buf = new char[64];
            var n = GetClassNameW(h, buf, buf.Length);
            if (IsWindowVisible(h) && new string(buf, 0, n) == "CabinetWClass") set.Add(h);
            return true;
        }, IntPtr.Zero);
        return set;
    }
}
