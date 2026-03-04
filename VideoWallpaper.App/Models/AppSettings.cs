namespace VideoWallpaper.App.Models;

public enum FitMode
{
    Fill,
    Fit,
    Stretch,
}

public enum PlaybackMode
{
    None,
    Local,
    YouTube,
}

public sealed class AppSettings
{
    public string? LastVideoPath { get; set; }

    public string? LastYouTubeUrl { get; set; }

    public bool Mute { get; set; } = true;

    public FitMode FitMode { get; set; } = FitMode.Fill;

    public bool PauseOnFullscreen { get; set; } = true;

    public bool PauseOnBattery { get; set; }

    public bool StartWithWindows { get; set; }

    public bool WallpaperModeEnabled { get; set; }

    public PlaybackMode LastPlaybackMode { get; set; } = PlaybackMode.None;

    public string? LastSource { get; set; }
}
