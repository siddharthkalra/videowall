using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
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
    private bool isInitializing;
    private bool suppressWallpaperToggleEvent;
    private bool isExiting;

    public MainWindow()
    {
        InitializeComponent();
        isInitializing = true;

        settings = SettingsStore.Load();
        if (settings.WallpaperModeEnabled)
        {
            settings.WallpaperModeEnabled = false;
            SettingsStore.Save(settings);
            AppLogger.Info("Startup safety: reset WallpaperModeEnabled=false.");
        }

        wallpaperWindow = new WallpaperMediaWindow();

        showHideMenuItem = new Forms.ToolStripMenuItem("Hide");
        muteMenuItem = new Forms.ToolStripMenuItem("Mute") { Checked = settings.Mute };
        wallpaperModeMenuItem = new Forms.ToolStripMenuItem("Enable Wallpaper Mode") { Checked = settings.WallpaperModeEnabled };
        trayMenu = new Forms.ContextMenuStrip();
        trayIcon = new Forms.NotifyIcon();

        InitializeTrayIcon();
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;

        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        suppressWallpaperToggleEvent = true;
        EnableWallpaperModeCheckBox.IsChecked = settings.WallpaperModeEnabled;
        suppressWallpaperToggleEvent = false;
        PauseOnFullscreenCheckBox.IsChecked = settings.PauseOnFullscreen;
        PauseOnBatteryCheckBox.IsChecked = settings.PauseOnBattery;

        policyMonitorTimer.Tick += PolicyMonitorTimer_OnTick;
        policyMonitorTimer.Start();

        SetStatus("Ready");
        AppLogger.Info("Main controller initialized.");
        isInitializing = false;
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

        var playYouTubeMenuItem = new Forms.ToolStripMenuItem("Play YouTube");
        playYouTubeMenuItem.Click += (_, _) => _ = Dispatcher.InvokeAsync(StartYouTubePlaybackAsync);

        var stopMenuItem = new Forms.ToolStripMenuItem("Stop");
        stopMenuItem.Click += (_, _) => Dispatcher.Invoke(StopAllPlayback);

        muteMenuItem.Click += (_, _) => _ = Dispatcher.InvokeAsync(ToggleMuteAsync);
        wallpaperModeMenuItem.Click += (_, _) => _ = Dispatcher.InvokeAsync(ToggleWallpaperModeAsync);

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);

        trayIcon.Text = "VideoWallpaper";
        trayIcon.Icon = System.Drawing.SystemIcons.Application;
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleWindowVisibility);
        trayMenu.Items.Add(showHideMenuItem);
        trayMenu.Items.Add(playLocalMenuItem);
        trayMenu.Items.Add(playYouTubeMenuItem);
        trayMenu.Items.Add(stopMenuItem);
        trayMenu.Items.Add(muteMenuItem);
        trayMenu.Items.Add(wallpaperModeMenuItem);
        trayMenu.Items.Add(exitMenuItem);
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;

        RefreshTrayMenuState();
        AppLogger.Info("Tray icon initialized.");
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

    private void SetYouTubeUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        var enteredUrl = PromptForText(
            "Set YouTube URL",
            "Enter YouTube URL:",
            settings.LastYouTubeUrl ?? "https://www.youtube.com/watch?v=");

        if (enteredUrl is null)
        {
            SetStatus("Set YouTube URL canceled.");
            return;
        }

        settings.LastYouTubeUrl = enteredUrl.Trim();
        settings.LastSource = settings.LastYouTubeUrl;
        SaveSettings();
        AppLogger.Info($"Updated YouTube URL: {settings.LastYouTubeUrl}");
        SetStatus("YouTube URL updated.");
    }

    private async void PlayLocalButton_OnClick(object sender, RoutedEventArgs e)
    {
        await StartLocalPlaybackAsync();
    }

    private async void PlayYouTubeButton_OnClick(object sender, RoutedEventArgs e)
    {
        await StartYouTubePlaybackAsync();
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

    private async void EnableWallpaperModeCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (isInitializing || suppressWallpaperToggleEvent)
        {
            return;
        }

        settings.WallpaperModeEnabled = EnableWallpaperModeCheckBox.IsChecked == true;
        SaveSettings();
        await ApplyWallpaperModeSettingAsync();
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
        suppressWallpaperToggleEvent = true;
        EnableWallpaperModeCheckBox.IsChecked = settings.WallpaperModeEnabled;
        suppressWallpaperToggleEvent = false;
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
                    suppressWallpaperToggleEvent = true;
                    EnableWallpaperModeCheckBox.IsChecked = false;
                    suppressWallpaperToggleEvent = false;
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

    private async Task StartYouTubePlaybackAsync()
    {
        SetStatus("YouTube playback is temporarily disabled to stabilize wallpaper mode.");
        await Task.CompletedTask;
    }

    private void EnsurePlaybackWindowVisible()
    {
        if (settings.WallpaperModeEnabled)
        {
            var enabled = EnsureWallpaperWindow().EnableWallpaperMode();
            if (!enabled)
            {
                settings.WallpaperModeEnabled = false;
                EnableWallpaperModeCheckBox.IsChecked = false;
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

    private string? PromptForText(string title, string prompt, string defaultValue)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Width = 520,
            Height = 190,
            ShowInTaskbar = false,
        };

        var root = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var promptBlock = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8),
        };
        System.Windows.Controls.Grid.SetRow(promptBlock, 0);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(0, 0, 0, 12),
            MinWidth = 460,
        };
        System.Windows.Controls.Grid.SetRow(textBox, 1);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var okButton = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80,
            IsCancel = true,
        };

        okButton.Click += (_, _) => dialog.DialogResult = true;
        cancelButton.Click += (_, _) => dialog.DialogResult = false;

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
        root.Children.Add(promptBlock);
        root.Children.Add(textBox);
        root.Children.Add(buttonPanel);

        dialog.Content = root;
        textBox.Focus();
        textBox.SelectAll();

        return dialog.ShowDialog() == true ? textBox.Text : null;
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
