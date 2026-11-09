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
    private Screenshots.ScreenshotController? _screenshots;
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
                .Add(ItemShowInFolder, "Show in folder")
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
            case ItemShowInFolder: RunShell("explorer.exe", $"/select,\"{vm.Path}\""); break;
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

    private static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        RunShell("explorer.exe", $"\"{folder}\"");
    }

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
        _monitor.Dispose();
        _screenshots?.Dispose();
        _tray?.Dispose();
        _thumbnails.Dispose();
        _fan?.Close();
    }
}
