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
    private readonly Grid root;
    private MediaElement mediaElement;
    private readonly DispatcherTimer playbackHealthTimer;
    private string? currentVideoPath;
    private FitMode currentFitMode = FitMode.Fill;
    private bool isMuted = true;
    private bool isPaused;
    private TimeSpan lastObservedPosition = TimeSpan.Zero;
    private int stalledTickCount;
    private DateTime lastProgressUtc = DateTime.UtcNow;
    private DateTime lastRestartUtc = DateTime.UtcNow;
    private DateTime lastSourceSetUtc = DateTime.UtcNow;
    private DateTime recoveryWindowStartUtc = DateTime.UtcNow;
    private int recoveryCountInWindow;
    private bool isRecovering;
    private bool hasMediaOpenedForCurrentSource;
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

        root = new Grid();
        mediaElement = CreateMediaElement();
        root.Children.Add(mediaElement);
        Content = root;

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
            lastProgressUtc = DateTime.UtcNow;
            lastRestartUtc = DateTime.UtcNow;
            lastSourceSetUtc = DateTime.UtcNow;
            hasMediaOpenedForCurrentSource = false;
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
            lastProgressUtc = DateTime.UtcNow;
            hasMediaOpenedForCurrentSource = false;
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
        if (currentPlaybackMode != PlaybackMode.Local || isPaused || string.IsNullOrWhiteSpace(currentVideoPath) || isRecovering)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;

        if (!hasMediaOpenedForCurrentSource)
        {
            if ((nowUtc - lastSourceSetUtc) > TimeSpan.FromSeconds(30))
            {
                AttemptRecovery("media did not open in time", rebuildPlayer: true, force: true);
            }

            return;
        }

        if (!mediaElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var current = mediaElement.Position;

        var advanced = Math.Abs((current - lastObservedPosition).TotalMilliseconds) > 120;
        if (advanced)
        {
            stalledTickCount = 0;
            lastObservedPosition = current;
            lastProgressUtc = nowUtc;
            return;
        }

        if ((nowUtc - lastRestartUtc) <= TimeSpan.FromSeconds(10))
        {
            return;
        }

        stalledTickCount++;
        if (stalledTickCount < 8 && (nowUtc - lastProgressUtc) <= TimeSpan.FromSeconds(12))
        {
            return;
        }

        AttemptRecovery("playback stall detected", rebuildPlayer: false);
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
        hasMediaOpenedForCurrentSource = false;
        lastSourceSetUtc = DateTime.UtcNow;
        mediaElement.Play();
        stalledTickCount = 0;
        lastObservedPosition = TimeSpan.Zero;
        lastProgressUtc = DateTime.UtcNow;
        lastRestartUtc = DateTime.UtcNow;
    }

    private MediaElement CreateMediaElement()
    {
        var element = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.UniformToFill,
            ScrubbingEnabled = false,
        };

        element.MediaOpened += (_, _) =>
        {
            stalledTickCount = 0;
            lastObservedPosition = mediaElement.Position;
            lastProgressUtc = DateTime.UtcNow;
            hasMediaOpenedForCurrentSource = true;
            AppLogger.Info("WallpaperMediaWindow: media opened.");
        };
        element.MediaEnded += (_, _) =>
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
        element.MediaFailed += (_, e) =>
        {
            AppLogger.Error($"WallpaperMediaWindow: local media failed: {e.ErrorException?.Message}", e.ErrorException);
            AttemptRecovery("media failure", rebuildPlayer: true, force: true);
        };

        return element;
    }

    private void RecreateMediaElement()
    {
        try
        {
            mediaElement.Stop();
            mediaElement.Source = null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("WallpaperMediaWindow: failed while disposing old media element.", ex);
        }

        root.Children.Clear();
        mediaElement = CreateMediaElement();
        root.Children.Add(mediaElement);
    }

    private void AttemptRecovery(string reason, bool rebuildPlayer, bool force = false)
    {
        if (isRecovering)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(currentVideoPath))
        {
            return;
        }

        isRecovering = true;
        try
        {
            var nowUtc = DateTime.UtcNow;
            if (!force && (nowUtc - lastRestartUtc) < TimeSpan.FromSeconds(20))
            {
                return;
            }

            if ((nowUtc - recoveryWindowStartUtc) > TimeSpan.FromMinutes(1))
            {
                recoveryWindowStartUtc = nowUtc;
                recoveryCountInWindow = 0;
            }

            recoveryCountInWindow++;
            if (recoveryCountInWindow > 10)
            {
                AppLogger.Error($"WallpaperMediaWindow: too many recovery attempts in one minute; keeping playback stopped. Last reason: {reason}");
                StopPlayback();
                return;
            }

            AppLogger.Info($"WallpaperMediaWindow: attempting recovery ({reason}), rebuild={rebuildPlayer}.");
            if (rebuildPlayer)
            {
                RecreateMediaElement();
            }

            RestartCurrentVideo();
        }
        catch (Exception ex)
        {
            AppLogger.Error("WallpaperMediaWindow: recovery attempt failed.", ex);
        }
        finally
        {
            isRecovering = false;
        }
    }
}
