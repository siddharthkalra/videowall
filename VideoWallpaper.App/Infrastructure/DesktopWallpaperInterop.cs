using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoWallpaper.App.Infrastructure;

internal static class DesktopWallpaperInterop
{
    private const uint SpawnWorkerWMessage = 0x052C;
    private static readonly IntPtr SpawnWParam = new(0xD);
    private const uint SendMessageTimeoutNormal = 0x0000;
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsCaption = 0x00C00000;
    private const int WsThickframe = 0x00040000;
    private const int WsMinimizebox = 0x00020000;
    private const int WsMaximizebox = 0x00010000;
    private const int WsSysmenu = 0x00080000;
    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;
    private const int SwShownoactivate = 4;
    private const int SwRestore = 9;
    private const int DwmwaCloaked = 14;

    public static bool TryAttachToDesktop(IntPtr wallpaperWindowHandle, out IntPtr workerWHandle)
    {
        workerWHandle = IntPtr.Zero;
        AppLogger.Info("Wallpaper interop: locating Progman.");

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            AppLogger.Error("Wallpaper interop: Progman not found.");
            return false;
        }

        AppLogger.Info("Wallpaper interop: sending WorkerW spawn message (0x052C).");
        // Standard WorkerW spawn sequence on modern Windows shells.
        _ = SendMessageTimeout(
            progman,
            SpawnWorkerWMessage,
            SpawnWParam,
            IntPtr.Zero,
            SendMessageTimeoutNormal,
            1000,
            out _);
        _ = SendMessageTimeout(
            progman,
            SpawnWorkerWMessage,
            SpawnWParam,
            new IntPtr(1),
            SendMessageTimeoutNormal,
            1000,
            out _);

        workerWHandle = FindWallpaperHostWindow(progman);
        if (workerWHandle == IntPtr.Zero)
        {
            AppLogger.Info("Wallpaper interop: specific WorkerW not found, falling back to Progman.");
            workerWHandle = progman;
        }

        if (workerWHandle == IntPtr.Zero)
        {
            AppLogger.Error("Wallpaper interop: WorkerW not found.");
            return false;
        }

        ApplyWallpaperWindowStyles(wallpaperWindowHandle);
        AppLogger.Info($"Wallpaper interop: WorkerW found ({workerWHandle}). Setting parent.");
        var previousParent = SetParent(wallpaperWindowHandle, workerWHandle);
        var setParentError = Marshal.GetLastWin32Error();

        var parentAfterAttach = GetParent(wallpaperWindowHandle);
        if (previousParent == IntPtr.Zero && parentAfterAttach != workerWHandle && setParentError != 0)
        {
            AppLogger.Error($"Wallpaper interop: SetParent failed. Win32={setParentError}");
            return false;
        }

        if (parentAfterAttach != workerWHandle)
        {
            AppLogger.Info(
                $"Wallpaper interop: parent verification differs. Expected={workerWHandle}, Actual={parentAfterAttach}");
        }

        AppLogger.Info("Wallpaper interop: parent set successfully.");
        return true;
    }

    public static void DetachFromDesktop(IntPtr wallpaperWindowHandle)
    {
        AppLogger.Info("Wallpaper interop: detaching wallpaper window from WorkerW.");
        _ = SetParent(wallpaperWindowHandle, IntPtr.Zero);
        ApplyStandaloneWindowStyles(wallpaperWindowHandle);
    }

    public static void MoveToBottomAndResize(IntPtr windowHandle, int x, int y, int width, int height)
    {
        _ = SetWindowPos(
            windowHandle,
            HwndBottom,
            x,
            y,
            width,
            height,
            SwpNoActivate | SwpShowWindow);
    }

    public static void SetClickThrough(IntPtr windowHandle, bool enabled)
    {
        var currentStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        var updatedStyle = enabled
            ? currentStyle | WsExNoactivate
            : currentStyle & ~WsExNoactivate;

        _ = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(updatedStyle));
        AppLogger.Info($"Wallpaper interop: click-through {(enabled ? "enabled" : "disabled")}.");
    }

    public static void ApplyWallpaperWindowStyles(IntPtr windowHandle)
    {
        var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        style |= WsChild;
        style &= ~WsPopup;
        style &= ~WsCaption;
        style &= ~WsThickframe;
        style &= ~WsMinimizebox;
        style &= ~WsMaximizebox;
        style &= ~WsSysmenu;
        _ = SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(style));

        var exStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        exStyle |= WsExToolwindow;
        exStyle &= ~WsExAppwindow;
        _ = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(exStyle));
        RefreshNonClientFrame(windowHandle);
    }

    public static void ApplyStandaloneWindowStyles(IntPtr windowHandle)
    {
        var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        style &= ~WsChild;
        style |= WsPopup;
        style &= ~WsCaption;
        style &= ~WsThickframe;
        style &= ~WsMinimizebox;
        style &= ~WsMaximizebox;
        style &= ~WsSysmenu;
        _ = SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(style));

        var exStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        exStyle |= WsExToolwindow;
        exStyle &= ~WsExAppwindow;
        _ = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(exStyle));
        RefreshNonClientFrame(windowHandle);
    }

    private static void RefreshNonClientFrame(IntPtr windowHandle)
    {
        _ = SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoActivate | SwpFramechanged);
    }

    public static IntPtr GetParentHandle(IntPtr windowHandle)
    {
        return GetParent(windowHandle);
    }

    public static bool IsWindowMinimized(IntPtr windowHandle)
    {
        return IsIconic(windowHandle);
    }

    public static bool IsWindowVisibleNative(IntPtr windowHandle)
    {
        return IsWindowVisible(windowHandle);
    }

    public static bool IsWindowValid(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero && IsWindow(windowHandle);
    }

    public static bool IsWindowCloaked(IntPtr windowHandle)
    {
        try
        {
            var cloaked = 0;
            var result = DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out cloaked, sizeof(int));
            return result == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    public static void RestoreWindowNoActivate(IntPtr windowHandle)
    {
        _ = ShowWindow(windowHandle, SwRestore);
        _ = ShowWindow(windowHandle, SwShownoactivate);
    }

    private static IntPtr FindWallpaperHostWindow(IntPtr progman)
    {
        // Find the top-level window containing desktop icons.
        var defViewParent = IntPtr.Zero;

        EnumWindows((topWindow, _) =>
        {
            var shellView = FindWindowEx(topWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                defViewParent = topWindow;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (defViewParent == IntPtr.Zero)
        {
            AppLogger.Info("Wallpaper interop: SHELLDLL_DefView parent not found.");
            return IntPtr.Zero;
        }

        // Preferred: WorkerW immediately after the desktop-icon host.
        var workerW = FindWindowEx(IntPtr.Zero, defViewParent, "WorkerW", null);
        if (workerW != IntPtr.Zero)
        {
            AppLogger.Info($"Wallpaper interop: selected WorkerW sibling {workerW}.");
            return workerW;
        }

        // Some builds host the icons under Progman directly.
        if (defViewParent == progman)
        {
            AppLogger.Info("Wallpaper interop: desktop icons hosted by Progman; searching plain WorkerW host.");
            var plainWorkerW = FindPlainWorkerW();
            if (plainWorkerW != IntPtr.Zero)
            {
                AppLogger.Info($"Wallpaper interop: selected plain WorkerW {plainWorkerW}.");
                return plainWorkerW;
            }

            AppLogger.Info("Wallpaper interop: no plain WorkerW found; using Progman host.");
            return progman;
        }

        AppLogger.Info("Wallpaper interop: searching plain WorkerW fallback host.");
        var fallbackWorkerW = FindPlainWorkerW();
        if (fallbackWorkerW != IntPtr.Zero)
        {
            AppLogger.Info($"Wallpaper interop: selected plain WorkerW fallback {fallbackWorkerW}.");
            return fallbackWorkerW;
        }

        AppLogger.Info("Wallpaper interop: no suitable WorkerW host found.");
        return IntPtr.Zero;
    }

    private static IntPtr FindPlainWorkerW()
    {
        var result = IntPtr.Zero;
        var bestArea = 0;

        EnumWindows((topWindow, _) =>
        {
            var classNameBuilder = new StringBuilder(256);
            _ = GetClassName(topWindow, classNameBuilder, classNameBuilder.Capacity);
            var className = classNameBuilder.ToString();
            if (!string.Equals(className, "WorkerW", StringComparison.Ordinal))
            {
                return true;
            }

            var shellView = FindWindowEx(topWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero)
            {
                if (!IsWindowVisible(topWindow))
                {
                    return true;
                }

                if (!GetWindowRect(topWindow, out var rect))
                {
                    return true;
                }

                var width = Math.Max(0, rect.Right - rect.Left);
                var height = Math.Max(0, rect.Bottom - rect.Top);
                var area = width * height;
                if (area > bestArea)
                {
                    bestArea = area;
                    result = topWindow;
                }
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(
        IntPtr parentHandle,
        IntPtr childAfter,
        string className,
        string? windowTitle);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);


    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out int pvAttribute,
        int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
