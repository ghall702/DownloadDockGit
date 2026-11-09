using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DownloadDock.Core.Tests;
using DownloadDock.Dragging;
using DownloadDock.Integration;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace DownloadDock.Windows.Tests;

/// <summary>
/// End-to-end tests against the REAL DownloadDock.exe, with the test acting as Finder for Windows:
/// a Finder client talks to DownloadDock's pipe, and a mock Finder server receives DownloadDock's calls.
/// </summary>
[Collection("Desktop")]
public class FinderIntegrationTests
{
    private static string? FindAppExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DownloadDock.sln"))) dir = dir.Parent;
        if (dir is null) return null;
        var config = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var exe = Path.Combine(dir.FullName, "src", "DownloadDock", "bin", config, "net10.0-windows", "DownloadDock.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static void KillRunningInstances()
    {
        foreach (var p in Process.GetProcessesByName("DownloadDock"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
            p.Dispose();
        }
    }

    private static Process StartApp(string exe, params string[] args)
    {
        Directory.CreateDirectory(DownloadDock.Services.KnownFolders.GetDownloadsFolder());
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static async Task<IntegrationClient> ConnectAsFinder(TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            var c = new IntegrationClient(new IntegrationClientOptions
            {
                PipeName = IntegrationEndpoints.DownloadDockPipeName(),
                ClientName = IntegrationProtocol.FinderClientName,
                ClientVersion = "0.1.0-test",
                Capabilities = new[] { IntegrationCapabilities.OpenFolder, IntegrationCapabilities.RevealFile, IntegrationCapabilities.FilesReceived },
                RequestTimeout = TimeSpan.FromSeconds(10),
            });
            try { await c.ConnectAsync(); return c; }
            catch (IntegrationException) when (DateTime.UtcNow < deadline) { await c.DisposeAsync(); await Task.Delay(250); }
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [SkippableFact]
    public async Task Real_app_serves_finder_commands_events_and_calls_finder_back()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        var exe = FindAppExe();
        Skip.If(exe is null, "DownloadDock.exe not built");
        KillRunningInstances();

        // A mock Finder for Windows, listening where the real Finder would.
        var received = new ConcurrentQueue<FilesReceivedRequest>();
        await using var mockFinder = new IntegrationServer(new IntegrationServerOptions
        {
            PipeName = IntegrationEndpoints.FinderPipeName(),
            ServerName = IntegrationProtocol.FinderClientName,
            ServerVersion = "0.1.0-test",
            Capabilities = new[] { IntegrationCapabilities.OpenFolder, IntegrationCapabilities.RevealFile, IntegrationCapabilities.FilesReceived },
        });
        mockFinder.Handle(IntegrationCommands.FilesReceived, ctx =>
        {
            var r = ctx.GetPayload<FilesReceivedRequest>();
            received.Enqueue(r);
            return Task.FromResult<object?>(new FilesReceivedResult { Accepted = r.Files.Count });
        });
        mockFinder.Handle(IntegrationCommands.OpenFolder, _ => Task.FromResult<object?>(new FinderOpenResult()));
        mockFinder.Handle(IntegrationCommands.RevealFile, _ => Task.FromResult<object?>(new FinderOpenResult { State = "selected" }));
        mockFinder.Start();

        using var app = StartApp(exe!, "--background");
        try
        {
            await using var finder = await ConnectAsFinder(TimeSpan.FromSeconds(30));
            Assert.Equal("DownloadDock", finder.Server!.ServerName);
            Assert.Equal(app.Id, finder.Server.ProcessId);
            Assert.Contains(IntegrationCapabilities.ShowFan, finder.Server.Capabilities);

            // DownloadDock connects back to the (mock) Finder.
            Assert.True(await WaitFor(() => mockFinder.Peers.Any(p => p.ClientName == IntegrationProtocol.DownloadDockClientName), 15000),
                "DownloadDock did not connect to Finder's pipe");
            Assert.Equal(app.Id, mockFinder.Peers.First(p => p.ClientName == IntegrationProtocol.DownloadDockClientName).ProcessId);

            var events = new ConcurrentQueue<IntegrationEvent>();
            finder.EventReceived += events.Enqueue;
            await finder.SubscribeAsync(new[] { IntegrationEvents.FanOpened, IntegrationEvents.FanClosed, IntegrationEvents.DownloadDetected });

            var status = (await finder.SendAsync<DownloadDockStatus>(IntegrationCommands.GetStatus))!;
            Assert.Equal("running", status.State);
            Assert.Equal(IntegrationProtocol.Version, status.ProtocolVersion);
            Assert.False(string.IsNullOrEmpty(status.AppVersion));
            Assert.True(Directory.Exists(status.DownloadsFolder), $"downloads folder {status.DownloadsFolder}");
            Assert.False(status.FanOpen);

            // Show the fan (twice: the second request must not create a duplicate), then close it.
            AllowSetForegroundWindow(app.Id);
            Assert.Equal("opened", (await finder.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan))!.State);
            Assert.Equal("alreadyOpen", (await finder.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan))!.State);
            Assert.True((await finder.SendAsync<DownloadDockStatus>(IntegrationCommands.GetStatus))!.FanOpen);
            Assert.True(await WaitFor(() => events.Any(e => e.Name == IntegrationEvents.FanOpened), 5000), "no fanOpened event");
            Assert.Equal("closed", (await finder.SendAsync<CloseFanResult>(IntegrationCommands.CloseFan))!.State);
            Assert.True(await WaitFor(() => events.Any(e => e.Name == IntegrationEvents.FanClosed), 5000), "no fanClosed event");

            // A real new download in the real Downloads folder → downloadDetected (from DownloadDock's own watcher).
            var download = Path.Combine(status.DownloadsFolder!, $"dd-integration-{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(download, "hello from the integration test");
                Assert.True(await WaitFor(() => events.Any(e => e.Name == IntegrationEvents.DownloadDetected
                    && string.Equals(e.GetPayload<FileEventPayload>()!.Path, download, StringComparison.OrdinalIgnoreCase)), 15000), "no downloadDetected event");
                // DownloadDock also notified Finder? No: Finder subscribed to downloadDetected, so that would be a duplicate.
                await Task.Delay(1000);
                Assert.DoesNotContain(received, r => r.Files.Any(f => string.Equals(f.Path, download, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                File.Delete(download);
            }

            // Screenshot (Win+Shift+3) → Finder not subscribed to screenshots → finder.filesReceived arrives.
            Keys.Chord(0x5B, 0x10, 0x33);
            Assert.True(await WaitFor(() => received.Any(r => r.Files.Any(f => f.Kind == ReceivedFileKinds.Screenshot)), 20000), "screenshot was not reported to Finder");
            var shot = received.SelectMany(r => r.Files).First(f => f.Kind == ReceivedFileKinds.Screenshot);
            Assert.True(File.Exists(shot.Path));
            Assert.EndsWith(".png", shot.Path);

            // Now subscribe to screenshot events: the next one arrives as an event and NOT also as filesReceived.
            await finder.SubscribeAsync(new[] { IntegrationEvents.ScreenshotCaptured });
            var before = received.Count;
            await Task.Delay(1500);
            Keys.Chord(0x5B, 0x10, 0x33);
            Assert.True(await WaitFor(() => events.Any(e => e.Name == IntegrationEvents.ScreenshotCaptured), 20000), "no screenshotCaptured event");
            await Task.Delay(1500);
            Assert.Equal(before, received.Count);
            var shotEvent = events.First(e => e.Name == IntegrationEvents.ScreenshotCaptured).GetPayload<FileEventPayload>()!;
            Assert.Equal("fullscreen", shotEvent.Source);
            Assert.True(File.Exists(shotEvent.Path));

            // Settings: open, then a second request reports it's already open.
            AllowSetForegroundWindow(app.Id);
            Assert.Equal("opened", (await finder.SendAsync<OpenSettingsResult>(IntegrationCommands.OpenSettings))!.State);
            Assert.Equal("alreadyOpen", (await finder.SendAsync<OpenSettingsResult>(IntegrationCommands.OpenSettings))!.State);

            // Nothing dangerous is exposed.
            Assert.Equal(IntegrationErrorCodes.UnknownCommand, (await Assert.ThrowsAsync<IntegrationException>(
                () => finder.SendAsync<object>("downloadDock.deleteFile", new { path = @"C:\Windows\win.ini" }))).Code);
        }
        finally
        {
            try { app.Kill(); app.WaitForExit(5000); } catch (Exception) { }
        }
    }

    [SkippableFact]
    public async Task Finder_reconnects_after_downloaddock_restarts_and_command_line_forwarding_works()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        var exe = FindAppExe();
        Skip.If(exe is null, "DownloadDock.exe not built");
        KillRunningInstances();

        // Finder starts FIRST, before DownloadDock.
        await using var finder = new ResilientIntegrationClient(new IntegrationClientOptions
        {
            PipeName = IntegrationEndpoints.DownloadDockPipeName(),
            ClientName = IntegrationProtocol.FinderClientName,
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
        }, new[] { IntegrationEvents.FanOpened })
        {
            MinRetryDelay = TimeSpan.FromMilliseconds(300),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
        };
        finder.Start();
        Assert.True(await WaitFor(() => finder.State == IntegrationConnectionState.Unavailable, 5000));

        var app = StartApp(exe!, "--background");
        try
        {
            Assert.True(await WaitFor(() => finder.IsConnected, 30000), "Finder did not connect after DownloadDock started");
            var firstPid = finder.Server!.ProcessId;

            // A second launch with --open-settings forwards to the running instance and exits.
            using (var second = StartApp(exe!, "--open-settings"))
            {
                Assert.True(second.WaitForExit(15000), "second instance should exit");
            }
            Assert.Equal("alreadyOpen", (await finder.SendAsync<OpenSettingsResult>(IntegrationCommands.OpenSettings))!.State);

            // DownloadDock crashes / restarts.
            app.Kill();
            app.WaitForExit(5000);
            Assert.True(await WaitFor(() => !finder.IsConnected, 10000));
            await Assert.ThrowsAsync<IntegrationException>(() => finder.SendAsync<DownloadDockStatus>(IntegrationCommands.GetStatus));

            app.Dispose();
            app = StartApp(exe!, "--background");
            Assert.True(await WaitFor(() => finder.IsConnected, 30000), "Finder did not reconnect after DownloadDock restarted");
            Assert.NotEqual(firstPid, finder.Server!.ProcessId);
            Assert.Equal("running", (await finder.SendAsync<DownloadDockStatus>(IntegrationCommands.GetStatus))!.State);
        }
        finally
        {
            try { app.Kill(); app.WaitForExit(5000); } catch (Exception) { }
            app.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>Drops from a Finder-style source that provides only the Shell IDList format (no CF_HDROP).</summary>
[Collection("Desktop")]
public class FinderStyleDropTests
{
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Gets the raw CIDA bytes the Shell produces for a file.</summary>
    private static byte[] ShellIdListFor(string path)
    {
        var obj = FileDragSource.CreateShellDataObject(new[] { path })!;
        var fmt = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)Native.RegisterClipboardFormatW(FileDropReader.ShellIdListFormat)),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };
        obj.GetData(ref fmt, out var medium);
        try
        {
            var size = (int)GlobalSize(medium.unionmember);
            var p = Native.GlobalLock(medium.unionmember);
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(p, bytes, 0, size);
                return bytes;
            }
            finally { Native.GlobalUnlock(medium.unionmember); }
        }
        finally { Native.ReleaseStgMedium(ref medium); }
    }

    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr h);

    [Fact]
    public void Shell_idlist_only_data_resolves_to_real_paths()
    {
        using var f = new TestFolder();
        var a = f.File("Résumé – 日本語.pdf", "x");
        Sta.Run(() =>
        {
            var cida = ShellIdListFor(a);
            Assert.Equal(new[] { Path.GetFullPath(a) }, FileDropReader.PathsFromCida(cida), StringComparer.OrdinalIgnoreCase);

            var data = new DataObject();
            data.SetData(FileDropReader.ShellIdListFormat, new MemoryStream(cida));
            Assert.False(data.GetDataPresent(DataFormats.FileDrop));
            Assert.True(FileDropReader.HasFiles(data));
            Assert.Equal(new[] { Path.GetFullPath(a) }, FileDropReader.GetFiles(data)!, StringComparer.OrdinalIgnoreCase);

            Assert.Empty(FileDropReader.PathsFromCida(new byte[] { 1, 2, 3 }));                 // garbage
            Assert.Empty(FileDropReader.PathsFromCida(BitConverter.GetBytes(int.MaxValue)));    // absurd count
        });
    }

    [SkippableFact]
    public void Finder_style_drag_without_cf_hdrop_drops_onto_the_fan()
    {
        Skip.IfNot(Input.HasInteractiveDesktop(), "No interactive desktop");
        using var f = new TestFolder();
        var downloads = Directory.CreateDirectory(Path.Combine(f.Path, "Downloads")).FullName;
        var file = f.File("From Finder.png", "png");
        string[]? dropped = null;
        Sta.Run(() =>
        {
            var thumbs = new Thumbnails.ThumbnailService(Path.Combine(Path.GetTempPath(), "dd-finder-drop-" + Guid.NewGuid()));
            var fan = new Fan.FanWindow(thumbs);
            var anchor = Fan.FanAnchorResolver.Resolve(null, new Interop.POINT(760, 700));
            fan.Open(anchor, Array.Empty<DownloadDock.Core.Downloads.DownloadEntry>(), 0, downloads,
                new DownloadDock.Core.Settings.AppSettings { AnimationSpeed = DownloadDock.Core.Settings.AnimationSpeed.Off }, dark: false, forDrop: true);
            fan.FilesDropped += files => dropped = files;
            Pump(500);
            try
            {
                var view = ((System.Windows.Controls.Canvas)fan.Content).Children.OfType<Fan.FanItemView>().Single();
                var centre = view.PointToScreen(new Point(view.PivotX, view.ItemHeight / 2));
                var target = new Native.POINT { X = (int)centre.X, Y = (int)centre.Y };

                // A Finder-like drag source: WPF DataObject carrying ONLY "Shell IDList Array".
                var data = new DataObject();
                data.SetData(FileDropReader.ShellIdListFormat, new MemoryStream(ShellIdListFor(file)));
                var source = new Window
                {
                    WindowStyle = WindowStyle.None, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                    Background = Brushes.Teal, Left = -32000, Top = -32000, Width = 10, Height = 10,
                };
                var hwnd = new WindowInteropHelper(source).EnsureHandle();
                source.Show();
                Native.SetWindowPos(hwnd, IntPtr.Zero, 150, 200, 200, 200, 0x0040);
                Pump(300);
                var start = new Native.POINT { X = 250, Y = 300 };
                Input.MoveTo(start);
                Pump(100);
                Input.LeftDown();
                Pump(100);
                var mover = new Thread(() =>
                {
                    Thread.Sleep(250);
                    for (var i = 1; i <= 30; i++)
                    {
                        Input.MoveTo(new Native.POINT { X = start.X + (target.X - start.X) * i / 30, Y = start.Y + (target.Y - start.Y) * i / 30 });
                        Thread.Sleep(25);
                    }
                    for (var i = 0; i < 4; i++) { Input.MoveTo(new Native.POINT { X = target.X + (i % 2 == 0 ? 2 : -2), Y = target.Y }); Thread.Sleep(60); }
                    Input.LeftUp();
                }) { IsBackground = true };
                mover.Start();
                var effect = System.Windows.DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
                mover.Join(5000);
                Pump(300);
                source.Close();
                Assert.Equal(DragDropEffects.Copy, effect);
            }
            finally
            {
                if (fan.IsOpen) fan.CloseFan(animate: false);
                fan.Close();
                thumbs.Dispose();
            }
        });
        Assert.NotNull(dropped);
        Assert.Equal(new[] { Path.GetFullPath(file) }, dropped!, StringComparer.OrdinalIgnoreCase);
    }
}
