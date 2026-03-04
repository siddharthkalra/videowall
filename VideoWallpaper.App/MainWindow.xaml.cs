using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoWallpaper.App.Infrastructure;
using VideoWallpaper.App.Models;
using Forms = System.Windows.Forms;

namespace VideoWallpaper.App;

public partial class MainWindow : Window
{
    private const int HotkeyIdExit = 0x514;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkQ = 0x51;

    private AppSettings settings = new();
    private WallpaperMediaWindow wallpaperWindow;

    private readonly Forms.NotifyIcon trayIcon;
    private readonly Forms.ContextMenuStrip trayMenu;
    private readonly Forms.ToolStripMenuItem showHideMenuItem;
    private readonly Forms.ToolStripMenuItem muteMenuItem;
    private readonly Forms.ToolStripMenuItem wallpaperModeMenuItem;

    private readonly DispatcherTimer policyMonitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim wallpaperTransitionLock = new(1, 1);
    private bool isPolicyTickRunning;
    private bool isAutoPausedByPolicy;
    private bool isExiting;

    public MainWindow()
    {
        InitializeComponent();

        settings = SettingsStore.Load();
        if (settings.WallpaperModeEnabled)
        {
            settings.WallpaperModeEnabled = false;
            SettingsStore.Save(settings);
            AppLogger.Info("Startup safety: reset WallpaperModeEnabled=false.");
        }

        wallpaperWindow = new WallpaperMediaWindow();

        var windowIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(windowIconPath))
        {
            Icon = BitmapFrame.Create(new Uri(windowIconPath, UriKind.Absolute));
        }

        showHideMenuItem = new Forms.ToolStripMenuItem("Hide");
        muteMenuItem = new Forms.ToolStripMenuItem("Mute") { Checked = settings.Mute };
        wallpaperModeMenuItem = new Forms.ToolStripMenuItem("Enable Wallpaper Mode") { Checked = settings.WallpaperModeEnabled };
        trayMenu = new Forms.ContextMenuStrip();
        trayIcon = new Forms.NotifyIcon();

        InitializeTrayIcon();
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;

        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        PauseOnFullscreenCheckBox.IsChecked = settings.PauseOnFullscreen;
        PauseOnBatteryCheckBox.IsChecked = settings.PauseOnBattery;

        policyMonitorTimer.Tick += PolicyMonitorTimer_OnTick;
        policyMonitorTimer.Start();

        SetStatus("Ready");
        AppLogger.Info("Main controller initialized.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);

        var handle = new WindowInteropHelper(this).Handle;
        var registered = RegisterHotKey(handle, HotkeyIdExit, ModControl | ModShift, VkQ);
        if (!registered)
        {
            AppLogger.Error($"Hotkey registration failed for Ctrl+Shift+Q. Win32={Marshal.GetLastWin32Error()}");
            SetStatus("Could not register Ctrl+Shift+Q hotkey.");
            return;
        }

        AppLogger.Info("Registered hotkey Ctrl+Shift+Q for app exit.");
    }

    private void InitializeTrayIcon()
    {
        showHideMenuItem.Click += (_, _) => ToggleWindowVisibility();

        var playLocalMenuItem = new Forms.ToolStripMenuItem("Play Local");
        playLocalMenuItem.Click += (_, _) => _ = Dispatcher.InvokeAsync(StartLocalPlaybackAsync);

        var stopMenuItem = new Forms.ToolStripMenuItem("Stop");
        stopMenuItem.Click += (_, _) => Dispatcher.Invoke(StopAllPlayback);

        muteMenuItem.Click += (_, _) => _ = Dispatcher.InvokeAsync(ToggleMuteAsync);
        wallpaperModeMenuItem.Click += (_, _) => _ = Dispatcher.InvokeAsync(ToggleWallpaperModeAsync);

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);

        trayIcon.Text = "VideoWallpaper";
        trayIcon.Icon = LoadTrayIcon();
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleWindowVisibility);
        trayMenu.Items.Add(showHideMenuItem);
        trayMenu.Items.Add(playLocalMenuItem);
        trayMenu.Items.Add(stopMenuItem);
        trayMenu.Items.Add(muteMenuItem);
        trayMenu.Items.Add(wallpaperModeMenuItem);
        trayMenu.Items.Add(exitMenuItem);
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;

        RefreshTrayMenuState();
        AppLogger.Info("Tray icon initialized.");
    }

    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var appIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(appIconPath))
            {
                var icon = new System.Drawing.Icon(appIconPath, new System.Drawing.Size(16, 16));
                AppLogger.Info($"Tray icon loaded from app icon: {appIconPath}");
                return icon;
            }

            var trayIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app_tray.ico");
            if (File.Exists(trayIconPath))
            {
                var icon = new System.Drawing.Icon(trayIconPath, new System.Drawing.Size(16, 16));
                AppLogger.Info($"Tray icon loaded from tray icon: {trayIconPath}");
                return icon;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to load custom tray icon; falling back to system icon.", ex);
        }

        return System.Drawing.SystemIcons.Application;
    }

    private async void PolicyMonitorTimer_OnTick(object? sender, EventArgs e)
    {
        if (isPolicyTickRunning)
        {
            return;
        }

        isPolicyTickRunning = true;
        try
        {
            var pauseForFullscreen = settings.PauseOnFullscreen && IsExternalFullscreenForegroundWindow();
            var pauseForBattery = settings.PauseOnBattery && IsOnBatteryPower();
            var shouldPause = pauseForFullscreen || pauseForBattery;

            if (shouldPause && !isAutoPausedByPolicy)
            {
                await EnsureWallpaperWindow().PausePlaybackAsync();
                isAutoPausedByPolicy = true;
                var reason = pauseForFullscreen ? "fullscreen app" : "battery power";
                SetStatus($"Playback paused due to {reason}.");
                AppLogger.Info($"Policy pause active: {reason}.");
            }
            else if (!shouldPause && isAutoPausedByPolicy)
            {
                await EnsureWallpaperWindow().ResumePlaybackAsync();
                isAutoPausedByPolicy = false;
                SetStatus("Playback resumed.");
                AppLogger.Info("Policy pause cleared.");
            }

            // Keep the timer lightweight. Wallpaper host re-attach is only done on explicit
            // wallpaper mode transitions to avoid UI hangs from repeated Win32 desktop probing.
        }
        catch (Exception ex)
        {
            AppLogger.Error("Policy monitor tick failed.", ex);
        }
        finally
        {
            isPolicyTickRunning = false;
        }
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (isExiting)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
        SetStatus("Minimized to tray.");
        AppLogger.Info("Controller close intercepted. Minimized to tray.");
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = UnregisterHotKey(handle, HotkeyIdExit);

        policyMonitorTimer.Stop();
        trayIcon.Visible = false;
        trayIcon.Dispose();
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            HideToTray();
            return;
        }

        ShowFromTray();
    }

    private void HideToTray()
    {
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
        RefreshTrayMenuState();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RefreshTrayMenuState();
    }

    private void RefreshTrayMenuState()
    {
        showHideMenuItem.Text = IsVisible && WindowState != WindowState.Minimized ? "Hide" : "Show";
        muteMenuItem.Checked = settings.Mute;
        wallpaperModeMenuItem.Checked = settings.WallpaperModeEnabled;
    }

    private void PickVideoButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Video File",
            Filter = "Video files|*.mp4;*.webm;*.mov",
            Multiselect = false,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            settings.LastVideoPath = dialog.FileName;
            settings.LastSource = dialog.FileName;
            SaveSettings();
            AppLogger.Info($"Selected local video: {settings.LastVideoPath}");
            SetStatus($"Selected local video: {settings.LastVideoPath}");
            return;
        }

        SetStatus("Pick video canceled.");
    }

    private async void PlayLocalButton_OnClick(object sender, RoutedEventArgs e)
    {
        await StartLocalPlaybackAsync();
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        StopAllPlayback();
    }

    private void HideUiButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideToTray();
        SetStatus("UI hidden to tray.");
    }

    private void StartWithWindowsCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        SaveSettings();
        SetStatus("Start with Windows setting will be implemented in the next step.");
    }

    private void PauseOnFullscreenCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        settings.PauseOnFullscreen = PauseOnFullscreenCheckBox.IsChecked == true;
        SaveSettings();
        SetStatus($"Pause on fullscreen {(settings.PauseOnFullscreen ? "enabled" : "disabled")}.");
    }

    private void PauseOnBatteryCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        settings.PauseOnBattery = PauseOnBatteryCheckBox.IsChecked == true;
        SaveSettings();
        SetStatus($"Pause on battery {(settings.PauseOnBattery ? "enabled" : "disabled")}.");
    }

    private async Task ToggleMuteAsync()
    {
        settings.Mute = !settings.Mute;
        await EnsureWallpaperWindow().ApplyMuteAsync(settings.Mute);
        SaveSettings();
        SetStatus(settings.Mute ? "Muted." : "Unmuted.");
        RefreshTrayMenuState();
    }

    private async Task ToggleWallpaperModeAsync()
    {
        settings.WallpaperModeEnabled = !settings.WallpaperModeEnabled;
        SaveSettings();
        await ApplyWallpaperModeSettingAsync();
    }

    private async Task ApplyWallpaperModeSettingAsync()
    {
        await wallpaperTransitionLock.WaitAsync();
        try
        {
            if (settings.WallpaperModeEnabled)
            {
                var enabled = EnsureWallpaperWindow().EnableWallpaperMode();
                if (!enabled)
                {
                    settings.WallpaperModeEnabled = false;
                    SaveSettings();
                    EnsureWallpaperWindow().DisableWallpaperMode(hideWindow: true);
                    SetStatus("Wallpaper mode failed. Playback host hidden.");
                    AppLogger.Error("Wallpaper mode enable failed; fallback applied.");
                }
                else
                {
                    if (EnsureWallpaperWindow().CurrentPlaybackMode == PlaybackMode.None &&
                        settings.LastPlaybackMode == PlaybackMode.Local &&
                        !string.IsNullOrWhiteSpace(settings.LastVideoPath) &&
                        File.Exists(settings.LastVideoPath))
                    {
                        var resumed = await EnsureWallpaperWindow().PlayLocalAsync(
                            settings.LastVideoPath,
                            settings.FitMode,
                            settings.Mute);
                        if (resumed)
                        {
                            SetStatus("Wallpaper mode enabled. Resumed local video.");
                            AppLogger.Info("Wallpaper mode enabled and local video resumed.");
                        }
                        else
                        {
                            SetStatus("Wallpaper mode enabled, but resume failed. Click Play Local.");
                            AppLogger.Error("Wallpaper mode enabled but resume of last local video failed.");
                        }
                    }
                    else
                    {
                        SetStatus("Wallpaper mode enabled.");
                        AppLogger.Info("Wallpaper mode enabled.");
                    }
                }
            }
            else
            {
                EnsureWallpaperWindow().DisableWallpaperMode(hideWindow: true);
                SetStatus("Wallpaper mode disabled.");
                AppLogger.Info("Wallpaper mode disabled.");
            }

            RefreshTrayMenuState();
        }
        finally
        {
            wallpaperTransitionLock.Release();
        }
    }

    private async Task StartLocalPlaybackAsync()
    {
        if (string.IsNullOrWhiteSpace(settings.LastVideoPath))
        {
            SetStatus("No local video selected. Click Pick Video first.");
            return;
        }

        if (!File.Exists(settings.LastVideoPath))
        {
            SetStatus($"File not found: {settings.LastVideoPath}");
            return;
        }

        EnsurePlaybackWindowVisible();
        var ok = await EnsureWallpaperWindow().PlayLocalAsync(settings.LastVideoPath, settings.FitMode, settings.Mute);

        if (!ok)
        {
            SetStatus("Failed to play local video.");
            return;
        }

        settings.LastPlaybackMode = PlaybackMode.Local;
        settings.LastSource = settings.LastVideoPath;
        SaveSettings();

        Activate();
        SetStatus(settings.WallpaperModeEnabled ? "Playing local wallpaper..." : "Playing local (window mode)...");
    }

    private void EnsurePlaybackWindowVisible()
    {
        if (settings.WallpaperModeEnabled)
        {
            var enabled = EnsureWallpaperWindow().EnableWallpaperMode();
            if (!enabled)
            {
                settings.WallpaperModeEnabled = false;
                SaveSettings();
                EnsureWallpaperWindow().DisableWallpaperMode(hideWindow: false);
                EnsureWallpaperWindow().ShowAsStandalone();
                SetStatus("Wallpaper mode failed. Switched to window mode.");
            }

            return;
        }

        EnsureWallpaperWindow().DisableWallpaperMode(hideWindow: false);
        EnsureWallpaperWindow().ShowAsStandalone();
    }

    private void StopAllPlayback()
    {
        EnsureWallpaperWindow().StopPlayback();
        isAutoPausedByPolicy = false;
        settings.LastPlaybackMode = PlaybackMode.None;
        SaveSettings();
        SetStatus("Stopped.");
    }

    private void ExitApplication()
    {
        isExiting = true;
        try
        {
            if (wallpaperWindow is not null && !wallpaperWindow.IsClosed)
            {
                wallpaperWindow.CleanupAndClose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exit cleanup failed.", ex);
        }

        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Save settings failed during exit.", ex);
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Controller close failed during exit.", ex);
        }
        finally
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyIdExit)
        {
            AppLogger.Info("Hotkey Ctrl+Shift+Q pressed. Exiting app.");
            ExitApplication();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void SaveSettings()
    {
        SettingsStore.Save(settings);
    }

    private WallpaperMediaWindow EnsureWallpaperWindow()
    {
        if (!wallpaperWindow.IsClosed)
        {
            return wallpaperWindow;
        }

        wallpaperWindow = new WallpaperMediaWindow();
        AppLogger.Info("Recreated wallpaper host window after it was closed.");
        return wallpaperWindow;
    }

    private bool IsOnBatteryPower()
    {
        return Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline;
    }

    private bool IsExternalFullscreenForegroundWindow()
    {
        var foregroundHandle = GetForegroundWindow();
        if (foregroundHandle == IntPtr.Zero)
        {
            return false;
        }

        var mainHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var wallpaperHandle = wallpaperWindow.IsClosed
            ? IntPtr.Zero
            : new WindowInteropHelper(wallpaperWindow).Handle;
        if (foregroundHandle == mainHandle || foregroundHandle == wallpaperHandle)
        {
            return false;
        }

        if (!GetWindowRect(foregroundHandle, out var windowRect))
        {
            return false;
        }

        var screen = Forms.Screen.FromHandle(foregroundHandle).Bounds;
        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;

        const int tolerance = 2;
        return Math.Abs(windowRect.Left - screen.Left) <= tolerance &&
               Math.Abs(windowRect.Top - screen.Top) <= tolerance &&
               Math.Abs(width - screen.Width) <= tolerance &&
               Math.Abs(height - screen.Height) <= tolerance;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
