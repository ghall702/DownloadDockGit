using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DownloadDock.Core.Downloads;
using DownloadDock.Core.Settings;
using DownloadDock.Dragging;
using DownloadDock.Fan;
using DownloadDock.Interop;
using DownloadDock.Services;
using DownloadDock.Settings;
using DownloadDock.Thumbnails;
using DownloadDock.Tray;
using DownloadDock.ViewModels;

namespace DownloadDock;

/// <summary>
/// Wires the pieces together: settings, the Downloads monitor, the thumbnail service, the tray
/// icon and the fan window. Lives for the whole process; closing the fan never exits the app.
/// </summary>
internal sealed class AppController : IDisposable
{
    /// <summary>How many newest files are tracked (the fan pages through these when they don't all fit).</summary>
    public const int TrackedFiles = 200;

    private const int CmdOpenFolder = 1;
    private const int CmdSettings = 2;
    private const int CmdRefresh = 3;
    private const int CmdExit = 4;
    private const int CmdStartup = 5;
    private const int CmdCaptureScreen = 6;
    private const int CmdCaptureRegion = 7;

    private const int ItemOpen = 101;
    private const int ItemOpenWith = 102;
    private const int ItemShowInFolder = 103;
    private const int ItemCopy = 104;
    private const int ItemCopyPath = 105;

    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _store = new(SettingsStore.DefaultPath);
    private readonly DownloadsMonitor _monitor = new();
    private readonly ThumbnailService _thumbnails = new();
    private TrayIcon? _tray;
    private FanWindow? _fan;
    private SettingsWindow? _settingsWindow;
    private readonly Core.Downloads.DwellTrigger _springLoad = new();
    private ForeignDragWatcher? _dragWatcher;
    private TrayDropCatcher? _catcher;
    private Screenshots.ScreenshotController? _screenshots;
    private DownloadDock.Core.Integration.DownloadDockIntegrationService? _integration;
    private DownloadDock.Core.Integration.FinderConnector? _finder;
    private Integration.AppSurface? _surface;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentlyAdded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string? AppVersion = typeof(AppController).Assembly.GetName().Version?.ToString(3);

    /// <summary>Raised on the UI thread when the Finder link changes (for the Settings window).</summary>
    public event Action<DownloadDock.Core.Integration.FinderStatus>? FinderStatusChanged;
    private AppSettings _settings = new();
    private string _folder = "";
    private DownloadsSnapshot? _snapshot;
    private bool _disposed;

    public AppController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public AppSettings Settings => _settings;

    public string Folder => _folder;

    public void Start(bool openFanNow)
    {
        _settings = _store.Load();
        var firstRun = !File.Exists(_store.FilePath);
        if (firstRun)
        {
            // Persist defaults and register for start-up once, as promised by the default setting.
            TrySave(_settings);
            StartupManager.SetEnabled(_settings.StartWithWindows);
        }

        _fan = new FanWindow(_thumbnails);
        _fan.OpenRequested += OnOpenRequested;
        _fan.ContextMenuRequested += OnItemContextMenu;
        _fan.DragFinished += (vm, result, effect) => Log.Info($"Drag of '{vm.Path}' finished: {result} (effect {effect})");
        _fan.FilesDropped += CopyIntoDownloads;
        _fan.FanOpened += () => { if (_surface is not null) _surface.FanOpen = true; _integration?.PublishFanState(true); };
        _fan.FanClosed += () => { if (_surface is not null) _surface.FanOpen = false; _integration?.PublishFanState(false); };
        FileDragSource.DragMoved += OnOwnDragMoved;
        FileDragSource.DragEnded += _ => _springLoad.Reset();
        StartForeignDragSupport();
        _ = _fan.Handle; // create the HWND up front so the first open is instant

        _tray = new TrayIcon("Downloads");
        _tray.Invoked += OnTrayInvoked;
        _tray.ContextMenuRequested += OnTrayContextMenu;
        _tray.ThemeChanged += () => _fan?.ApplyTheme(IsDark);
        _tray.DisplayChanged += () => { if (_fan?.IsOpen == true) _fan.CloseFan(animate: false); };
        _tray.Show();

        _monitor.SnapshotChanged += (_, snap) => _dispatcher.BeginInvoke(() => OnSnapshot(snap));
        _monitor.WatcherError += (_, ex) => Log.Error("Folder watcher error", ex);
        ApplyMonitorSettings();

        try
        {
            _screenshots = new Screenshots.ScreenshotController(_dispatcher, () => _settings);
            _screenshots.Start();
        }
        catch (Exception ex)
        {
            // The Downloads fan must keep working even if the screenshot feature can't start.
            Log.Error("Screenshot feature failed to start", ex);
            _screenshots = null;
        }

        StartIntegration();

        Log.Info($"Started. Folder: {_folder}");
        if (openFanNow)
            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => OpenFan(null));
    }

    private bool IsDark => ThemeService.IsDark(_settings.Theme);

    private void ApplyMonitorSettings()
    {
        _folder = string.IsNullOrWhiteSpace(_settings.DownloadsFolder) ? KnownFolders.GetDownloadsFolder() : _settings.DownloadsFolder!;
        _monitor.Start(_folder, TrackedFiles, _settings.IncludeSubfolders);
    }

    private void OnSnapshot(DownloadsSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        // Warm the previews for the files the fan will show first, so it opens with real thumbnails.
        var px = FanWindow.ThumbnailPixelsFor(_settings.ThumbnailSize, PrimaryDpiScale());
        _thumbnails.Prefetch(snapshot.Items.Take(_settings.VisibleFiles), px);
        _fan?.UpdateEntries(snapshot.Items, snapshot.TotalCount, snapshot.Folder);

        // Integration: report genuinely new downloads (from this same watcher – no second one).
        if (_integration is { } integration)
        {
            foreach (var e in integration.OnDownloadsSnapshot(snapshot.Folder, snapshot.Items, AlreadyReported))
                _finder?.NotifyFileReceived(e.FullPath, DownloadDock.Integration.ReceivedFileKinds.Download, e.Length);
        }
    }

    private bool AlreadyReported(string path)
    {
        foreach (var (p, t) in _recentlyAdded)
        {
            if (DateTime.UtcNow - t > TimeSpan.FromMinutes(2)) _recentlyAdded.TryRemove(p, out _);
        }
        return _recentlyAdded.ContainsKey(path) || _screenshots?.Store.IsOwnFile(path) == true;
    }

    private double PrimaryDpiScale()
    {
        if (_tray is null) return 1;
        var dpi = NativeMethods.GetDpiForWindow(_tray.Handle);
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    // ------------------------------------------------------------------ tray

    private void OnTrayInvoked(POINT anchor)
    {
        if (_fan is null) return;
        if (_fan.IsOpen && !_fan.IsClosing)
        {
            _fan.CloseFan();
            return;
        }
        // Clicking the tray icon while the fan is open first deactivates (and closes) the fan,
        // then delivers this click: treat that as "toggle closed", not "open again".
        if ((DateTime.UtcNow - _fan.LastClosedUtc).TotalMilliseconds < 400 || _fan.IsClosing)
            return;
        OpenFan(anchor);
    }

    public void OpenFanFromExternalLaunch()
    {
        if (_fan is { IsOpen: false }) OpenFan(null);
    }

    private void OpenFan(POINT? clickPoint)
    {
        if (_fan is null || _tray is null) return;
        var snapshot = _snapshot ?? _monitor.Current;
        if (snapshot is null || (DateTime.UtcNow - snapshot.ScannedUtc).TotalSeconds > 30)
        {
            // Make sure we're current (e.g. the watcher was idle on a network drive).
            try { snapshot = _monitor.ScanNow(); } catch (Exception ex) { Log.Error("Scan failed", ex); }
            _snapshot = snapshot;
        }
        var anchor = FanAnchorResolver.Resolve(_tray.GetIconRect(), clickPoint);
        _fan.Open(anchor, snapshot?.Items ?? Array.Empty<DownloadEntry>(), snapshot?.TotalCount ?? 0, _folder, _settings, IsDark);
    }

    // ------------------------------------------------------------------ dropping onto the dock

    /// <summary>
    /// While a file is dragged out of DownloadDock (e.g. a screenshot thumbnail), resting it on the tray
    /// icon springs the fan open so it can be dropped in – the notification area itself can't accept drops.
    /// </summary>
    private void OnOwnDragMoved(POINT p)
    {
        if (_fan is null || _tray is null || _fan.IsDragging) return;
        if (_fan.IsOpen && !_fan.IsClosing) { _springLoad.Reset(); return; }
        var inside = _tray.GetIconRect() is { } r && p.X >= r.Left - 6 && p.X < r.Right + 6 && p.Y >= r.Top - 6 && p.Y < r.Bottom + 6;
        if (_springLoad.Update(inside, DateTime.UtcNow))
            _dispatcher.BeginInvoke(() => OpenFanForDrop());
    }

    /// <summary>
    /// Lets files dragged from *other* apps (Explorer, browsers, mail…) be dropped on the tray icon: a
    /// mouse watcher notices a drag reaching the icon and arms a transparent drop catcher over it.
    /// </summary>
    private void StartForeignDragSupport()
    {
        try
        {
            _catcher = new TrayDropCatcher
            {
                Accepts = files => !Core.Downloads.DropIntoFolder.AllAlreadyIn(files, _folder),
            };
            _ = _catcher.Handle;
            _catcher.SpringRequested += () => OpenFanForDrop();
            _catcher.FilesDropped += files =>
            {
                CopyIntoDownloads(files);
                // Show the new download arriving at the bottom of the stack.
                if (_fan is { IsOpen: false }) OpenFanForDrop();
            };

            _dragWatcher = new ForeignDragWatcher(_dispatcher);
            _dragWatcher.ButtonPressed += () =>
            {
                _dragWatcher?.SetTarget(_tray?.GetIconRect());
                CloseInactiveFanOnOutsideClick();
            };
            _dragWatcher.DragApproachingTarget += () =>
            {
                if (_catcher is null || _tray?.GetIconRect() is not { } rect) return;
                if (_fan is { IsOpen: true, IsClosing: false }) return; // the fan itself is the target then
                _catcher.Arm(rect);
            };
            _dragWatcher.ButtonReleased += () => _catcher?.DisarmAfterRelease();
            _dragWatcher.SetTarget(_tray?.GetIconRect());
            if (!_dragWatcher.IsHooked) Log.Error("Dropping files from other apps on the tray icon is unavailable (mouse hook refused)");
        }
        catch (Exception ex)
        {
            Log.Error("Foreign drag support failed to start", ex);
        }
    }

    // ------------------------------------------------------------------ Finder for Windows integration

    private DownloadDock.Core.Integration.FinderIntegrationSettings FinderSettings() => new(
        _settings.FinderIntegrationEnabled, _settings.PreferFinderForFolders, _settings.PreferFinderForReveal, _settings.LaunchFinderIfNotRunning);

    /// <summary>
    /// Starts the integration endpoint (Finder → DownloadDock) and the Finder connector
    /// (DownloadDock → Finder). Both run on background threads; failures never affect the rest of the app.
    /// </summary>
    private void StartIntegration()
    {
        try
        {
            _surface = new Integration.AppSurface(_dispatcher, ShowFanForIntegration, CloseFanForIntegration, OpenSettingsForIntegration) { AppVersion = AppVersion };
            UpdateSurface();
            _finder = new DownloadDock.Core.Integration.FinderConnector(new Integration.WindowsFinderEnvironment(), FinderSettings, AppVersion,
                e => _integration?.FinderSubscribedTo(e) == true);
            _finder.StatusChanged += st => _dispatcher.BeginInvoke(() => FinderStatusChanged?.Invoke(st));
            _finder.Start();
            if (_settings.FinderIntegrationEnabled) StartIntegrationServer();
            RegisterForFinder();

            if (_screenshots is { } shots)
            {
                shots.Toasts.RevealInFolder = RevealAsync;
                shots.Toasts.ShowInFolderLabel = ShowInFolderLabel;
                shots.ScreenshotSaved += item => _ = AnnounceScreenshotAsync(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Finder integration failed to start (DownloadDock continues without it)", ex);
        }
    }

    private void StartIntegrationServer()
    {
        if (_integration is not null || _surface is null) return;
        _integration = new DownloadDock.Core.Integration.DownloadDockIntegrationService(_surface, AppVersion, Log.Info);
        _integration.FinderAnnounced += _ => _finder?.Nudge();
        _integration.Start();
        // Baseline for new-download detection.
        if (_snapshot is { } snap) _integration.OnDownloadsSnapshot(snap.Folder, snap.Items);
        Log.Info("Integration endpoint started: " + _integration.PipeName);
    }

    private void StopIntegrationServer()
    {
        var integration = _integration;
        _integration = null;
        if (integration is null) return;
        try { integration.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        Log.Info("Integration endpoint stopped");
    }

    private void UpdateSurface()
    {
        if (_surface is null) return;
        _surface.DownloadsFolder = _folder;
        _surface.ScreenshotsEnabled = _settings.ScreenshotsEnabled;
        _surface.ScreenshotFolder = _settings.ScreenshotFolder ?? Screenshots.ScreenshotStore.WindowsScreenshotsFolder();
        _surface.FanOpen = _fan?.IsOpen == true && !_fan.IsClosing;
    }

    /// <summary>Publishes where DownloadDock is installed (and whether Finder may start it) for Finder to read.</summary>
    private void RegisterForFinder()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !Path.GetFileName(exe).Equals("DownloadDock.exe", StringComparison.OrdinalIgnoreCase)) return;
            DownloadDock.Integration.AppRegistrationStore.Write(DownloadDock.Integration.AppRegistrationStore.DownloadDockKey,
                new DownloadDock.Integration.AppRegistration(exe, AppVersion, DownloadDock.Integration.IntegrationProtocol.Version, _settings.AllowFinderToLaunchDownloadDock));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("Could not write the integration registration", ex);
        }
    }

    private string ShowFanForIntegration()
    {
        if (_fan is null) return "unavailable";
        if (_fan.IsOpen && !_fan.IsClosing) return "alreadyOpen";
        OpenFan(null);
        return "opened";
    }

    private string CloseFanForIntegration()
    {
        if (_fan is not { IsOpen: true } || _fan.IsClosing) return "notOpen";
        _fan.CloseFan();
        return "closed";
    }

    private string OpenSettingsForIntegration()
    {
        var already = _settingsWindow is { IsLoaded: true };
        ShowSettings();
        return already ? "alreadyOpen" : "opened";
    }

    public void OpenSettingsFromExternalLaunch() => ShowSettings();

    /// <summary>
    /// A fan opened on Finder's request may not get keyboard focus (Windows foreground rules), so it
    /// would never see "deactivated". Close it on the next click outside it, as a focused fan would.
    /// </summary>
    private void CloseInactiveFanOnOutsideClick()
    {
        if (_fan is not { IsOpen: true } fan || fan.IsClosing || fan.IsActive || fan.IsDropMode || fan.IsDragging) return;
        NativeMethods.GetCursorPos(out var p);
        if (NativeMethods.GetWindowRect(fan.Handle, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return;
        if (_tray?.GetIconRect() is { } icon && p.X >= icon.Left && p.X < icon.Right && p.Y >= icon.Top && p.Y < icon.Bottom) return; // the tray click toggles it
        fan.CloseFan();
    }

    private async Task AnnounceScreenshotAsync(Screenshots.ScreenshotItem item)
    {
        try
        {
            var path = await item.FileTask.ConfigureAwait(false);
            var size = new FileInfo(path).Length;
            _recentlyAdded[path] = DateTime.UtcNow;
            _integration?.PublishScreenshotCaptured(path, item.Source, size);
            _finder?.NotifyFileReceived(path, DownloadDock.Integration.ReceivedFileKinds.Screenshot, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Could not announce screenshot", ex);
        }
    }

    internal void OpenFanForDrop(FanAnchor? anchorOverride = null)
    {
        if (_fan is null || _tray is null || (_fan.IsOpen && !_fan.IsClosing)) return;
        var snapshot = _snapshot ?? _monitor.Current;
        var anchor = anchorOverride ?? FanAnchorResolver.Resolve(_tray.GetIconRect(), null);
        Log.Info("Fan sprung open for a drop");
        _fan.Open(anchor, snapshot?.Items ?? Array.Empty<DownloadEntry>(), snapshot?.TotalCount ?? 0, _folder, _settings, IsDark, forDrop: true);
    }

    /// <summary>Copies dropped files into the Downloads folder (never moves, never overwrites).</summary>
    private void CopyIntoDownloads(string[] files)
    {
        var folder = _folder;
        _ = Task.Run(() =>
        {
            foreach (var source in files)
            {
                try
                {
                    if (!File.Exists(source)) continue; // folders and virtual items are not copied
                    var target = Core.Downloads.DropIntoFolder.UniqueCopyPath(folder, Path.GetFileName(source));
                    File.Copy(source, target, overwrite: false);
                    // A copy keeps the old write time; stamp it so it sorts as the newest download.
                    File.SetCreationTimeUtc(target, DateTime.UtcNow);
                    File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                    Log.Info($"Dropped onto the fan: copied {source} -> {target}");
                    _recentlyAdded[target] = DateTime.UtcNow;
                    var size = new FileInfo(target).Length;
                    _integration?.PublishFileAddedToDownloads(target, size);
                    _finder?.NotifyFileReceived(target, DownloadDock.Integration.ReceivedFileKinds.AddedToDownloads, size);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Error($"Could not copy dropped file {source}", ex);
                }
            }
            _monitor.RequestScan(immediate: true);
        });
    }

    private void OnTrayContextMenu(POINT pt)
    {
        if (_tray is null) return;
        _fan?.CloseFan(animate: false);
        var menu = new NativeMenu()
            .Add(CmdOpenFolder, "Open Downloads Folder", isDefault: true)
            .AddSeparator()
            .Add(CmdCaptureScreen, "Capture Screen\t" + ShortcutLabel(_settings.FullScreenShortcut), enabled: _screenshots is not null && _settings.ScreenshotsEnabled)
            .Add(CmdCaptureRegion, "Capture Area\t" + ShortcutLabel(_settings.RegionShortcut), enabled: _screenshots is not null && _settings.ScreenshotsEnabled)
            .AddSeparator()
            .Add(CmdSettings, "Settings…")
            .Add(CmdRefresh, "Refresh")
            .AddSeparator()
            .Add(CmdStartup, "Start with Windows", isChecked: StartupManager.IsEnabled())
            .AddSeparator()
            .Add(CmdExit, "Exit");
        var cmd = menu.Show(_tray.Handle, pt.X, pt.Y, ThemeService.SystemUsesDarkTheme());
        switch (cmd)
        {
            case CmdOpenFolder: OpenFolder(_folder); break;
            case CmdCaptureScreen: _ = _screenshots?.CaptureFullScreenAsync(); break;
            case CmdCaptureRegion: _ = _screenshots?.CaptureRegionAsync(); break;
            case CmdSettings: ShowSettings(); break;
            case CmdRefresh: Refresh(); break;
            case CmdStartup:
                var enable = !StartupManager.IsEnabled();
                StartupManager.SetEnabled(enable);
                _settings = _settings with { StartWithWindows = enable };
                TrySave(_settings);
                break;
            case CmdExit: Application.Current.Shutdown(); break;
        }
    }

    private static string ShortcutLabel(string shortcut) => string.IsNullOrEmpty(shortcut) ? "" : shortcut;

    private void Refresh()
    {
        _thumbnails.ClearAll();
        _snapshot = null;
        _monitor.RequestScan(immediate: true);
    }

    // ------------------------------------------------------------------ fan actions

    private void OnOpenRequested(FanItemViewModel vm)
    {
        if (vm.IsFolderItem)
        {
            OpenFolder(vm.Path);
        }
        else
        {
            OpenFile(vm.Path);
        }
        _fan?.CloseFan();
    }

    private void OnItemContextMenu(FanItemViewModel vm, POINT pt)
    {
        if (_fan is null) return;
        var menu = new NativeMenu();
        if (vm.IsFolderItem)
        {
            menu.Add(ItemOpen, "Open Downloads Folder", isDefault: true)
                .Add(CmdSettings, "Settings…")
                .Add(CmdRefresh, "Refresh");
        }
        else
        {
            menu.Add(ItemOpen, "Open", isDefault: true)
                .Add(ItemOpenWith, "Open with…")
                .Add(ItemShowInFolder, ShowInFolderLabel())
                .AddSeparator()
                .Add(ItemCopy, "Copy")
                .Add(ItemCopyPath, "Copy as path");
        }

        _fan.SuppressDeactivation = true;
        int cmd;
        try
        {
            cmd = menu.Show(_fan.Handle, pt.X, pt.Y, IsDark, alignAboveCursor: false);
        }
        finally
        {
            _fan.SuppressDeactivation = false;
        }

        switch (cmd)
        {
            case ItemOpen: OnOpenRequested(vm); return;
            case ItemOpenWith: RunShell("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {vm.Path}"); break;
            case ItemShowInFolder: _ = RevealAsync(vm.Path); break;
            case ItemCopy: CopyFiles(vm.Path); break;
            case ItemCopyPath: TrySetClipboardText($"\"{vm.Path}\""); break;
            case CmdSettings: _fan.CloseFan(); ShowSettings(); return;
            case CmdRefresh: Refresh(); break;
        }
        if (cmd is ItemOpenWith or ItemShowInFolder)
            _fan.CloseFan();
        else
            _fan.Activate();
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // No association: let the user pick an app.
            Log.Error($"Open failed for {path}", ex);
            RunShell("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            Log.Error($"Open failed for {path}", ex);
        }
    }

    private void OpenFolder(string folder) => _ = OpenFolderAsync(folder);

    /// <summary>Opens a folder in Finder (when preferred and available) or File Explorer – never on the UI thread.</summary>
    private async Task OpenFolderAsync(string folder)
    {
        if (_finder is { } finder)
        {
            var outcome = await Task.Run(() => finder.OpenFolderAsync(folder)).ConfigureAwait(true);
            Log.Info($"Open folder -> {outcome}");
            return;
        }
        if (Directory.Exists(folder)) RunShell("explorer.exe", $"\"{folder}\"");
    }

    /// <summary>"Show in folder": Finder when preferred and available, otherwise File Explorer (always works).</summary>
    private async Task RevealAsync(string path)
    {
        if (_finder is { } finder)
        {
            var outcome = await Task.Run(() => finder.RevealFileAsync(path)).ConfigureAwait(true);
            Log.Info($"Show in folder -> {outcome}");
            return;
        }
        RunShell("explorer.exe", $"/select,\"{path}\"");
    }

    private string ShowInFolderLabel() => _finder?.WouldUseFinderForReveal == true ? "Show in Finder" : "Show in folder";

    private static void RunShell(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Failed to run {exe} {args}", ex);
        }
    }

    /// <summary>Puts the real file on the clipboard (CF_HDROP, copy semantics) – an extra to drag-and-drop, not a replacement.</summary>
    private static void CopyFiles(string path)
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(NativeMethods.DROPEFFECT_COPY)));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("Clipboard copy failed", ex);
        }
    }

    private static void TrySetClipboardText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Log.Error("Clipboard failed", ex); }
    }

    // ------------------------------------------------------------------ settings

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        var vm = new SettingsViewModel(_settings with { StartWithWindows = StartupManager.IsEnabled() }, KnownFolders.GetDownloadsFolder(),
            Screenshots.ScreenshotStore.WindowsScreenshotsFolder(), _screenshots?.Hotkeys, _screenshots?.ShortcutProblems);
        _settingsWindow = new SettingsWindow(vm);
        vm.Saved += ApplySettings;
        vm.ClearCacheRequested += () => _thumbnails.ClearAll();
        if (_finder is { } connector)
        {
            vm.SetFinderStatus(connector.Status);
            Action<DownloadDock.Core.Integration.FinderStatus> onStatus = vm.SetFinderStatus;
            FinderStatusChanged += onStatus;
            _settingsWindow.Closed += (_, _) => FinderStatusChanged -= onStatus;
        }
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ApplySettings(AppSettings updated)
    {
        var old = _settings;
        _settings = updated.Normalize();
        TrySave(_settings);
        StartupManager.SetEnabled(_settings.StartWithWindows);

        if (!string.Equals(old.DownloadsFolder, _settings.DownloadsFolder, StringComparison.OrdinalIgnoreCase)
            || old.IncludeSubfolders != _settings.IncludeSubfolders)
        {
            _snapshot = null;
            ApplyMonitorSettings();
        }
        else if (_snapshot is not null)
        {
            OnSnapshot(_snapshot);
        }
        _fan?.ApplyTheme(IsDark);
        _screenshots?.ApplySettings();

        if (!string.Equals(old.DownloadsFolder, _settings.DownloadsFolder, StringComparison.OrdinalIgnoreCase))
            _integration?.PublishDownloadsFolderChanged(_folder);
        if (old.FinderIntegrationEnabled != _settings.FinderIntegrationEnabled)
        {
            if (_settings.FinderIntegrationEnabled) StartIntegrationServer();
            else StopIntegrationServer();
        }
        _finder?.SettingsChanged();
        UpdateSurface();
        RegisterForFinder();
    }

    private void TrySave(AppSettings settings)
    {
        try { _store.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Saving settings failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FileDragSource.DragMoved -= OnOwnDragMoved;
        StopIntegrationServer();
        try { _finder?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _dragWatcher?.Dispose();
        _catcher?.Close();
        _monitor.Dispose();
        _screenshots?.Dispose();
        _tray?.Dispose();
        _thumbnails.Dispose();
        _fan?.Close();
    }
}
