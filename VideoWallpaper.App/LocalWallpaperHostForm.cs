using System;
using System.IO;
using System.Threading.Tasks;
using VideoWallpaper.App.Infrastructure;
using VideoWallpaper.App.Models;
using Forms = System.Windows.Forms;
using Wv2 = Microsoft.Web.WebView2.WinForms;

namespace VideoWallpaper.App;

public sealed class LocalWallpaperHostForm : Forms.Form
{
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);

    private readonly Wv2.WebView2 webView;
    private IntPtr wallpaperHostHandle = IntPtr.Zero;
    private bool isWebViewInitialized;
    private bool isAttachedToWallpaperHost;
    private PlaybackMode currentPlaybackMode = PlaybackMode.None;
    private bool isClosed;
    private bool muted = true;
    private FitMode fitMode = FitMode.Fill;

    public LocalWallpaperHostForm()
    {
        StartPosition = Forms.FormStartPosition.Manual;
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        BackColor = System.Drawing.Color.Black;

        webView = new Wv2.WebView2
        {
            Dock = Forms.DockStyle.Fill,
        };
        webView.NavigationCompleted += WebViewOnNavigationCompleted;
        Controls.Add(webView);
    }

    public PlaybackMode CurrentPlaybackMode => currentPlaybackMode;

    public bool IsClosed => isClosed;

    public async Task<bool> PlayLocalAsync(string path, FitMode fit, bool mute)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AppLogger.Error($"LocalWallpaperHostForm: local file not found: {path}");
            return false;
        }

        if (!await EnsureWebViewInitializedAsync())
        {
            return false;
        }

        try
        {
            muted = mute;
            fitMode = fit;

            var fileUri = new Uri(path, UriKind.Absolute).AbsoluteUri;
            var objectFit = fit switch
            {
                FitMode.Fill => "cover",
                FitMode.Fit => "contain",
                FitMode.Stretch => "fill",
                _ => "cover",
            };

            var html = $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    html, body { margin: 0; width: 100%; height: 100%; background: #000; overflow: hidden; }
    video { width: 100%; height: 100%; object-fit: {{objectFit}}; background: #000; }
  </style>
</head>
<body>
  <video id="v" autoplay loop playsinline muted src="{{fileUri}}"></video>
  <script>
    const v = document.getElementById('v');
    v.addEventListener('ended', () => { v.currentTime = 0; v.play().catch(() => {}); });
    const tryPlay = () => v.play().catch(() => {});
    v.addEventListener('loadedmetadata', tryPlay);
    v.addEventListener('canplay', tryPlay);
    setTimeout(tryPlay, 120);
  </script>
</body>
</html>
""";

            webView.NavigateToString(html);
            _ = ApplyMuteAsync(mute);
            currentPlaybackMode = PlaybackMode.Local;
            AppLogger.Info($"LocalWallpaperHostForm: local playback started ({path}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: failed to play local video.", ex);
            return false;
        }
    }

    public Task<bool> PlayYouTubeAsync(string url, bool mute)
    {
        AppLogger.Info("LocalWallpaperHostForm: YouTube playback disabled in local-only stability mode.");
        return Task.FromResult(false);
    }

    public async Task ApplyMuteAsync(bool mute)
    {
        muted = mute;
        if (!isWebViewInitialized || webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var script = mute
                ? "const v=document.getElementById('v'); if(v){v.muted=true; v.volume=0;}"
                : "const v=document.getElementById('v'); if(v){v.muted=false; v.volume=1;}";
            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: failed to apply mute.", ex);
        }
    }

    public async Task PausePlaybackAsync()
    {
        if (!isWebViewInitialized || webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await webView.CoreWebView2.ExecuteScriptAsync("const v=document.getElementById('v'); if(v){v.pause();}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: failed to pause playback.", ex);
        }
    }

    public async Task ResumePlaybackAsync()
    {
        if (!isWebViewInitialized || webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await webView.CoreWebView2.ExecuteScriptAsync("const v=document.getElementById('v'); if(v){v.play();}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: failed to resume playback.", ex);
        }
    }

    public void StopPlayback()
    {
        try
        {
            if (isWebViewInitialized && webView.CoreWebView2 is not null)
            {
                webView.CoreWebView2.Navigate("about:blank");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: failed to stop playback.", ex);
        }

        currentPlaybackMode = PlaybackMode.None;
    }

    public bool EnsureWallpaperModeActive()
    {
        if (!isAttachedToWallpaperHost || isClosed || IsDisposed)
        {
            return false;
        }

        try
        {
            if (WindowState == Forms.FormWindowState.Minimized ||
                !DesktopWallpaperInterop.IsWindowVisibleNative(Handle) ||
                DesktopWallpaperInterop.IsWindowCloaked(Handle))
            {
                DesktopWallpaperInterop.RestoreWindowNoActivate(Handle);
            }

            if (!DesktopWallpaperInterop.IsWindowValid(wallpaperHostHandle))
            {
                AppLogger.Info("LocalWallpaperHostForm: wallpaper host became invalid. Reattaching.");
                if (!DesktopWallpaperInterop.TryAttachToDesktop(Handle, out wallpaperHostHandle))
                {
                    AppLogger.Error("LocalWallpaperHostForm: reattach failed.");
                    isAttachedToWallpaperHost = false;
                    return false;
                }
            }

            var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            DesktopWallpaperInterop.MoveToBottomAndResize(Handle, 0, 0, bounds.Width, bounds.Height);
            DesktopWallpaperInterop.SetClickThrough(Handle, true);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: EnsureWallpaperModeActive failed.", ex);
            return false;
        }
    }

    public bool EnableWallpaperMode()
    {
        ConfigureWallpaperModeAppearance();
        EnsureVisible();
        ResizeToPrimaryMonitor();

        if (!DesktopWallpaperInterop.TryAttachToDesktop(Handle, out wallpaperHostHandle))
        {
            AppLogger.Error("LocalWallpaperHostForm: failed to attach to desktop host.");
            return false;
        }

        DesktopWallpaperInterop.MoveToBottomAndResize(
            Handle,
            0,
            0,
            Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920,
            Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080);
        DesktopWallpaperInterop.SetClickThrough(Handle, true);

        isAttachedToWallpaperHost = true;
        AppLogger.Info($"LocalWallpaperHostForm: attached to desktop host ({wallpaperHostHandle}).");
        return true;
    }

    public void DisableWallpaperMode(bool hideWindow)
    {
        if (isClosed || IsDisposed)
        {
            return;
        }

        if (isAttachedToWallpaperHost)
        {
            if (IsHandleCreated)
            {
                DesktopWallpaperInterop.DetachFromDesktop(Handle);
            }
            isAttachedToWallpaperHost = false;
            wallpaperHostHandle = IntPtr.Zero;
        }

        if (hideWindow)
        {
            if (IsHandleCreated)
            {
                DesktopWallpaperInterop.SetClickThrough(Handle, false);
            }

            if (Visible)
            {
                Hide();
            }

            return;
        }

        ConfigureStandaloneAppearance();
        ShowAsStandalone();
    }

    public void ShowAsStandalone()
    {
        ConfigureStandaloneAppearance();
        EnsureVisible();
        WindowState = Forms.FormWindowState.Normal;
        var screenBounds = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        const int defaultWidth = 960;
        const int defaultHeight = 540;
        var width = Math.Min(defaultWidth, screenBounds.Width);
        var height = Math.Min(defaultHeight, screenBounds.Height);
        var x = screenBounds.Left + (screenBounds.Width - width) / 2;
        var y = screenBounds.Top + (screenBounds.Height - height) / 2;
        Bounds = new System.Drawing.Rectangle(x, y, width, height);
    }

    public void CleanupAndClose()
    {
        if (isClosed || IsDisposed)
        {
            return;
        }

        try
        {
            StopPlayback();
            DisableWallpaperMode(hideWindow: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: cleanup failed.", ex);
        }
        finally
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Error("LocalWallpaperHostForm: close failed during cleanup.", ex);
            }
        }
    }

    protected override void OnFormClosed(Forms.FormClosedEventArgs e)
    {
        isClosed = true;
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (isAttachedToWallpaperHost && m.Msg == WmNcHitTest)
        {
            m.Result = HtTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    private async Task<bool> EnsureWebViewInitializedAsync()
    {
        if (isWebViewInitialized && webView.CoreWebView2 is not null)
        {
            return true;
        }

        try
        {
            await webView.EnsureCoreWebView2Async();
            if (webView.CoreWebView2 is null)
            {
                AppLogger.Error("LocalWallpaperHostForm: WebView2 init returned null CoreWebView2.");
                return false;
            }

            var webSettings = webView.CoreWebView2.Settings;
            webSettings.IsStatusBarEnabled = false;
            webSettings.AreDefaultContextMenusEnabled = false;
            webSettings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
            webSettings.AreDevToolsEnabled = false;
#endif

            isWebViewInitialized = true;
            AppLogger.Info("LocalWallpaperHostForm: WebView2 initialized.");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: WebView2 initialization failed.", ex);
            return false;
        }
    }

    private void EnsureVisible()
    {
        if (!Visible)
        {
            Show();
        }
    }

    private void ResizeToPrimaryMonitor()
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        Bounds = bounds;
    }

    private void ConfigureWallpaperModeAppearance()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        if (IsHandleCreated)
        {
            DesktopWallpaperInterop.ApplyWallpaperWindowStyles(Handle);
        }
    }

    private void ConfigureStandaloneAppearance()
    {
        FormBorderStyle = Forms.FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = true;
        TopMost = false;
        if (IsHandleCreated)
        {
            DesktopWallpaperInterop.ApplyStandaloneWindowStyles(Handle);
            DesktopWallpaperInterop.SetClickThrough(Handle, false);
        }
    }

    private async void WebViewOnNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await webView.CoreWebView2.ExecuteScriptAsync(
                "const v=document.getElementById('v'); if(v){v.play().catch(()=>{});} ");
            await ApplyMuteAsync(muted);
        }
        catch (Exception ex)
        {
            AppLogger.Error("LocalWallpaperHostForm: post-navigation play kick failed.", ex);
        }
    }
}
