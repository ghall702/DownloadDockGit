using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DownloadDock.Core.Tests;
using DownloadDock.Dragging;
using DownloadDock.Interop;

namespace DownloadDock.Windows.Tests;

/// <summary>
/// Dropping files dragged from *another application* onto DownloadDock's tray icon. The drag source is
/// a separate PowerShell/WinForms process doing a standard OLE file drag (as Explorer or a browser
/// would); the "tray icon" is a fixed screen rectangle.
/// </summary>
[Collection("Desktop")]
public class ForeignDragTests
{
    private static readonly RECT Icon = new(700, 520, 740, 560);

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

    private const string DragSourceScript = """
        param([string]$File, [string]$Title)
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.StartPosition = 'Manual'
        $form.FormBorderStyle = 'None'
        $form.Location = New-Object System.Drawing.Point(120, 200)
        $form.Size = New-Object System.Drawing.Size(200, 200)
        $form.TopMost = $true
        $form.ShowInTaskbar = $false
        $form.BackColor = [System.Drawing.Color]::Orange
        $form.Add_MouseDown({
            $data = New-Object System.Windows.Forms.DataObject
            $files = New-Object System.Collections.Specialized.StringCollection
            [void]$files.Add($File)
            $data.SetFileDropList($files)
            [void]$form.DoDragDrop($data, [System.Windows.Forms.DragDropEffects]::Copy)
        })
        [System.Windows.Forms.Application]::Run($form)
        """;

    /// <summary>Starts the external drag-source app and returns its process and window rectangle.</summary>
    private static (Process Process, Native.RECT Rect)? StartForeignSource(string folder, string file)
    {
        var script = Path.Combine(folder, "dragsource.ps1");
        File.WriteAllText(script, DragSourceScript);
        var title = "DD foreign source " + Guid.NewGuid().ToString("N")[..6];
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -STA -ExecutionPolicy Bypass -File \"{script}\" -File \"{file}\" -Title \"{title}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi);
        if (p is null) return null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = IntPtr.Zero;
            Native.EnumWindows((h, _) =>
            {
                if (Native.IsWindowVisible(h) && Native.GetTitle(h) == title) { hwnd = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (hwnd != IntPtr.Zero)
            {
                Thread.Sleep(400);
                Native.GetWindowRect(hwnd, out var r);
                return (p, r);
            }
            Thread.Sleep(200);
        }
        try { p.Kill(); } catch (InvalidOperationException) { }
        return null;
    }

    /// <summary>Wires a catcher + watcher exactly as the app does, around the fake icon rectangle.</summary>
    private sealed class Rig : IDisposable
    {
        public TrayDropCatcher Catcher { get; } = new();
        public ForeignDragWatcher Watcher { get; }
        public int Springs;
        public string[]? Dropped;
        public bool EverArmed;

        public Rig()
        {
            _ = Catcher.Handle;
            Catcher.SpringRequested += () => Springs++;
            Catcher.FilesDropped += f => Dropped = f;
            Watcher = new ForeignDragWatcher(Dispatcher.CurrentDispatcher);
            Watcher.SetTarget(Icon);
            Watcher.DragApproachingTarget += () => { Catcher.Arm(Icon); EverArmed = true; };
            Watcher.ButtonReleased += () => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, Catcher.Disarm);
        }

        public void Dispose()
        {
            Watcher.Dispose();
            Catcher.Close();
        }
    }

    private static Task Gesture(Native.POINT from, Native.POINT to, int restMs)
        => Task.Run(() =>
        {
            Input.MoveTo(from);
            Thread.Sleep(150);
            Input.LeftDown();
            Thread.Sleep(150);
            for (var i = 1; i <= 25; i++)
            {
                Input.MoveTo(new Native.POINT { X = from.X + (to.X - from.X) * i / 25, Y = from.Y + (to.Y - from.Y) * i / 25 });
                Thread.Sleep(25);
            }
            // Rest on the icon with tiny movements (as a hand does), then release.
            var until = DateTime.UtcNow.AddMilliseconds(restMs);
            var k = 0;
            while (DateTime.UtcNow < until)
            {
                Input.MoveTo(new Native.POINT { X = to.X + (k++ % 2 == 0 ? 1 : -1), Y = to.Y });
                Thread.Sleep(60);
            }
            Input.LeftUp();
        });

    [SkippableFact]
    public void File_dragged_from_another_app_springs_the_fan_and_drops_on_the_tray_icon()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        var file = f.File("From Explorer.pdf", "%PDF-1.4");
        var source = StartForeignSource(f.Path, file);
        Skip.If(source is null, "Could not start the external drag source");
        var (process, rect) = source!.Value;
        try
        {
            Sta.Run(() =>
            {
                using var rig = new Rig();
                Assert.True(rig.Watcher.IsHooked);
                var from = new Native.POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
                var to = new Native.POINT { X = (Icon.Left + Icon.Right) / 2, Y = (Icon.Top + Icon.Bottom) / 2 };
                var gesture = Gesture(from, to, restMs: 1200);
                Assert.True(PumpUntil(() => gesture.IsCompleted && rig.Dropped is not null, 20000),
                    $"armed={rig.EverArmed} springs={rig.Springs} dropped={rig.Dropped is not null}");
                Assert.True(rig.EverArmed, "the catcher should be armed when the drag reaches the icon");
                Assert.True(rig.Springs >= 1, "resting on the icon should spring the fan open");
                Assert.Equal(new[] { file }, rig.Dropped!, StringComparer.OrdinalIgnoreCase);
                Assert.True(PumpUntil(() => !rig.Catcher.IsArmed, 3000), "catcher should disarm after the drag");
            });
            Assert.True(File.Exists(file), "the original stays where it was (copy)");
        }
        finally
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    [SkippableFact]
    public void Ordinary_mouse_drags_and_presses_on_the_icon_are_left_alone()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        Sta.Run(() =>
        {
            using var rig = new Rig();
            var plain = new Window
            {
                WindowStyle = WindowStyle.None, Topmost = true, ShowInTaskbar = false, Background = Brushes.Gray,
                Left = -32000, Top = -32000, Width = 10, Height = 10,
            };
            var hwnd = new System.Windows.Interop.WindowInteropHelper(plain).EnsureHandle();
            plain.Show();
            Native.SetWindowPos(hwnd, IntPtr.Zero, 120, 200, 200, 200, 0x0040);
            Pump(300);
            try
            {
                var to = new Native.POINT { X = (Icon.Left + Icon.Right) / 2, Y = (Icon.Top + Icon.Bottom) / 2 };

                // 1. A plain mouse drag (no files) reaching the icon: armed briefly, never springs or drops.
                var g1 = Gesture(new Native.POINT { X = 220, Y = 300 }, to, restMs: 800);
                Assert.True(PumpUntil(() => g1.IsCompleted, 15000));
                Pump(400);
                Assert.Equal(0, rig.Springs);
                Assert.Null(rig.Dropped);
                Assert.False(rig.Catcher.IsArmed, "catcher must not linger after the button is released");

                // 2. A press that starts on the icon (click / rearranging tray icons) never arms the catcher.
                rig.EverArmed = false;
                var g2 = Gesture(to, new Native.POINT { X = to.X - 150, Y = to.Y - 80 }, restMs: 200);
                Assert.True(PumpUntil(() => g2.IsCompleted, 15000));
                Pump(300);
                Assert.False(rig.EverArmed);
            }
            finally
            {
                plain.Close();
            }
        });
    }
}
