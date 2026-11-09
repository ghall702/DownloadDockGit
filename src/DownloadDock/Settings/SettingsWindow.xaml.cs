using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using DownloadDock.Core.Settings;
using DownloadDock.Interop;
using DownloadDock.Services;
using Microsoft.Win32;

namespace DownloadDock.Settings;

internal partial class SettingsWindow
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        // WPF's Windows 11 (Fluent) theme for native-looking controls; follows the chosen theme.
        ThemeMode = vm.Theme switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += Close;
        vm.BrowseRequested += PickFolder;
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/DownloadDock.ico"));
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException)
        {
        }
        SourceInitialized += (_, _) => ApplyWindowChrome();
    }

    private void ApplyWindowChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            var dark = ThemeService.IsDark(_vm.Theme) ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var corner = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }

    private string? PickFolder(string initial)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder DownloadDock shows",
            InitialDirectory = initial,
            Multiselect = false,
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OpenTaskbarSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not open taskbar settings", ex);
        }
    }
}
