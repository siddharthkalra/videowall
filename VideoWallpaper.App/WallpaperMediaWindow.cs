using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VideoWallpaper.App.Infrastructure;
using VideoWallpaper.App.Models;
using Forms = System.Windows.Forms;

namespace VideoWallpaper.App;

public sealed class WallpaperMediaWindow : Window
{
    private readonly MediaElement mediaElement;
    private readonly DispatcherTimer playbackHealthTimer;
    private string? currentVideoPath;
    private FitMode currentFitMode = FitMode.Fill;
    private bool isMuted = true;
    private bool isPaused;
    private TimeSpan lastObservedPosition = TimeSpan.Zero;
    private int stalledTickCount;
    private DateTime lastHardRefreshUtc = DateTime.UtcNow;
    private bool isAttachedToWallpaperHost;
    private bool isClosed;
    private PlaybackMode currentPlaybackMode = PlaybackMode.None;

    public WallpaperMediaWindow()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        AllowsTransparency = false;
        Background = System.Windows.Media.Brushes.Black;
        Topmost = false;
        Title = "VideoWallpaper Playback";

        mediaElement = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.UniformToFill,
            ScrubbingEnabled = false,
        };
        mediaElement.MediaOpened += (_, _) =>
        {
            stalledTickCount = 0;
            lastObservedPosition = TimeSpan.Zero;
            AppLogger.Info("WallpaperMediaWindow: media opened.");
        };
        mediaElement.MediaEnded += (_, _) =>
        {
            try
            {
                AppLogger.Info("WallpaperMediaWindow: media ended. Restarting.");
                RestartCurrentVideo();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WallpaperMediaWindow: loop restart failed.", ex);
            }
        };
        mediaElement.MediaFailed += (_, e) =>
        {
            AppLogger.Error($"WallpaperMediaWindow: local media failed: {e.ErrorException?.Message}", e.ErrorException);
        };

        Content = mediaElement;

        playbackHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        playbackHealthTimer.Tick += PlaybackHealthTimerOnTick;
        playbackHealthTimer.Start();
    }

    public PlaybackMode CurrentPlaybackMode => currentPlaybackMode;
    public bool IsClosed => isClosed;

    public Task<bool> PlayLocalAsync(string path, FitMode fit, bool mute)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AppLogger.Error($"WallpaperMediaWindow: local file not found: {path}");
            return Task.FromResult(false);
        }

        try
        {
            EnsureVisible();
            currentVideoPath = path;
            currentFitMode = fit;
            isMuted = mute;
            isPaused = false;
            stalledTickCount = 0;
            lastObservedPosition = TimeSpan.Zero;
            lastHardRefreshUtc = DateTime.UtcNow;
            mediaElement.Stop();
            mediaElement.Source = new Uri(path, UriKind.Absolute);
            mediaElement.Stretch = fit switch
            {
                FitMode.Fill => Stretch.UniformToFill,
                FitMode.Fit => Stretch.Uniform,
                FitMode.Stretch => Stretch.Fill,
                _ => Stretch.UniformToFill,
            };
            mediaElement.IsMuted = mute;
            mediaElement.Volume = mute ? 0 : 1;
            mediaElement.Play();

            currentPlaybackMode = PlaybackMode.Local;
            AppLogger.Info($"WallpaperMediaWindow: local playback started ({path}).");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("WallpaperMediaWindow: failed to play local video.", ex);
            return Task.FromResult(false);
        }
    }

    public Task<bool> PlayYouTubeAsync(string url, bool mute)
    {
        AppLogger.Info("WallpaperMediaWindow: YouTube playback disabled in local-only stability mode.");
        return Task.FromResult(false);
    }

    public Task ApplyMuteAsync(bool mute)
    {
        isMuted = mute;
        mediaElement.IsMuted = mute;
        mediaElement.Volume = mute ? 0 : 1;
        return Task.CompletedTask;
    }

    public Task PausePlaybackAsync()
    {
        try
        {
            isPaused = true;
            mediaElement.Pause();
        }
        catch (Exception ex)
        {
            AppLogger.Error("WallpaperMediaWindow: pause failed.", ex);
        }

        return Task.CompletedTask;
    }

    public Task ResumePlaybackAsync()
    {
        try
        {
            isPaused = false;
            mediaElement.Play();
        }
        catch (Exception ex)
        {
            AppLogger.Error("WallpaperMediaWindow: resume failed.", ex);
        }

        return Task.CompletedTask;
    }

    public void StopPlayback()
    {
        try
        {
            isPaused = false;
            mediaElement.Stop();
            mediaElement.Source = null;
            currentVideoPath = null;
            stalledTickCount = 0;
            lastObservedPosition = TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            AppLogger.Error("WallpaperMediaWindow: stop failed.", ex);
        }

        currentPlaybackMode = PlaybackMode.None;
    }

    public bool EnsureWallpaperModeActive()
    {
        return isAttachedToWallpaperHost;
    }

    public bool EnableWallpaperMode()
    {
        ConfigureWallpaperAppearance();
        EnsureVisible();
        ResizeToPrimaryMonitor();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            AppLogger.Error("WallpaperMediaWindow: window handle unavailable.");
            return false;
        }

        if (!DesktopWallpaperInterop.TryAttachToDesktop(handle, out var workerW))
        {
            AppLogger.Error("WallpaperMediaWindow: failed to attach to desktop host.");
            return false;
        }

        var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        DesktopWallpaperInterop.MoveToBottomAndResize(handle, 0, 0, bounds.Width, bounds.Height);
        DesktopWallpaperInterop.SetClickThrough(handle, true);
        isAttachedToWallpaperHost = true;
        AppLogger.Info($"WallpaperMediaWindow: attached to desktop host ({workerW}).");
        return true;
    }

    public void DisableWallpaperMode(bool hideWindow)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (isAttachedToWallpaperHost && handle != IntPtr.Zero)
        {
            DesktopWallpaperInterop.DetachFromDesktop(handle);
            DesktopWallpaperInterop.SetClickThrough(handle, false);
            isAttachedToWallpaperHost = false;
        }

        ConfigureStandaloneAppearance();

        if (hideWindow)
        {
            Hide();
            return;
        }

        ShowAsStandalone();
    }

    public void ShowAsStandalone()
    {
        EnsureVisible();
        WindowState = WindowState.Maximized;
    }

    public void CleanupAndClose()
    {
        try
        {
            StopPlayback();
            DisableWallpaperMode(hideWindow: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("WallpaperMediaWindow: cleanup failed.", ex);
        }

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        playbackHealthTimer.Stop();
        isClosed = true;
        base.OnClosed(e);
    }

    private void EnsureVisible()
    {
        if (!IsVisible)
        {
            Show();
        }
    }

    private void ResizeToPrimaryMonitor()
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void ConfigureWallpaperAppearance()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = false;
    }

    private void ConfigureStandaloneAppearance()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Topmost = false;
    }

    private void PlaybackHealthTimerOnTick(object? sender, EventArgs e)
    {
        if (currentPlaybackMode != PlaybackMode.Local || isPaused || string.IsNullOrWhiteSpace(currentVideoPath))
        {
            return;
        }

        if (!mediaElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        // Conservative periodic refresh to prevent long-running decoder stalls in MediaElement.
        if ((DateTime.UtcNow - lastHardRefreshUtc) >= TimeSpan.FromSeconds(90))
        {
            AppLogger.Info("WallpaperMediaWindow: periodic refresh.");
            RestartCurrentVideo();
            lastHardRefreshUtc = DateTime.UtcNow;
            stalledTickCount = 0;
            lastObservedPosition = TimeSpan.Zero;
            return;
        }

        var current = mediaElement.Position;
        var duration = mediaElement.NaturalDuration.TimeSpan;
        var remaining = duration - current;

        // Some codecs fail to raise MediaEnded reliably when attached or running long.
        // Proactively restart as we approach the end.
        if (remaining.TotalMilliseconds <= 400)
        {
            AppLogger.Info("WallpaperMediaWindow: near end-of-file. Restarting.");
            RestartCurrentVideo();
            lastHardRefreshUtc = DateTime.UtcNow;
            stalledTickCount = 0;
            lastObservedPosition = TimeSpan.Zero;
            return;
        }

        var advanced = Math.Abs((current - lastObservedPosition).TotalMilliseconds) > 120;
        var nearBeginning = current.TotalSeconds < 1.0;
        if (advanced || nearBeginning)
        {
            stalledTickCount = 0;
            lastObservedPosition = current;
            return;
        }

        stalledTickCount++;
        if (stalledTickCount < 5)
        {
            return;
        }

        AppLogger.Info("WallpaperMediaWindow: playback stall detected. Restarting media.");
        RestartCurrentVideo();
        lastHardRefreshUtc = DateTime.UtcNow;
        stalledTickCount = 0;
        lastObservedPosition = TimeSpan.Zero;
    }

    private void RestartCurrentVideo()
    {
        if (string.IsNullOrWhiteSpace(currentVideoPath))
        {
            return;
        }

        mediaElement.Stop();
        mediaElement.Source = null;
        mediaElement.Source = new Uri(currentVideoPath, UriKind.Absolute);
        mediaElement.Stretch = currentFitMode switch
        {
            FitMode.Fill => Stretch.UniformToFill,
            FitMode.Fit => Stretch.Uniform,
            FitMode.Stretch => Stretch.Fill,
            _ => Stretch.UniformToFill,
        };
        mediaElement.IsMuted = isMuted;
        mediaElement.Volume = isMuted ? 0 : 1;
        mediaElement.Position = TimeSpan.Zero;
        mediaElement.Play();
    }
}
