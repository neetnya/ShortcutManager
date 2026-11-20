using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ShortcutManager;

/// <summary>可选的窗口视觉效果与样式控制。全部为尽力而为，失败不影响功能。</summary>
internal static class WindowEffects
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 启用 Windows 11 原生圆角（不使用背景模糊以保持内容清晰与启动速度）。
    /// </summary>
    public static void ApplyRoundedCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* Windows 10 上不支持，忽略 */ }
    }

    // ==================================================================
    //  去掉最大化
    // ==================================================================

    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZEBOX = 0x0001_0000L;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int SC_MAXIMIZE = 0xF030;
    private const int HTCAPTION = 2;

    /// <summary>
    /// 禁用最大化：窗口保留拖拽改尺寸，但最大化按钮、标题栏双击、系统菜单里的“最大化”都不再有效。
    /// 三层保险：样式位 → 拦 SC_MAXIMIZE/双击标题栏 → 万一还是进了最大化就立刻还原。
    /// </summary>
    public static void DisableMaximize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var style = GetStyle(hwnd);
            SetStyle(hwnd, style & ~WS_MAXIMIZEBOX);
        }
        catch { /* 改不了样式也别让程序起不来 */ }

        HwndSource? source = null;
        HwndSourceHook? hook = null;
        try
        {
            source = HwndSource.FromHwnd(hwnd);
            if (source is not null)
            {
                hook = (IntPtr _, int msg, IntPtr w, IntPtr _, ref bool handled) =>
                {
                    switch (msg)
                    {
                        case WM_SYSCOMMAND when ((int)w & 0xFFF0) == SC_MAXIMIZE:
                            handled = true;
                            return IntPtr.Zero;

                        case WM_NCLBUTTONDBLCLK when (int)w == HTCAPTION:
                            handled = true;
                            return IntPtr.Zero;
                    }
                    return IntPtr.Zero;
                };
                source.AddHook(hook);
            }
        }
        catch { /* ignore */ }

        void OnStateChanged(object? sender, EventArgs e)
        {
            if (window.WindowState == WindowState.Maximized)
                window.WindowState = WindowState.Normal;
        }

        window.StateChanged += OnStateChanged;
        window.Closed += (_, _) =>
        {
            window.StateChanged -= OnStateChanged;
            if (source is not null && hook is not null)
            {
                try { source.RemoveHook(hook); } catch { /* ignore */ }
            }
        };
    }

    /// <summary>窗口是否还带最大化按钮（自动化验证用）。</summary>
    internal static bool HasMaximizeBox(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        try { return (GetStyle(hwnd) & WS_MAXIMIZEBOX) != 0; }
        catch { return false; }
    }

    private static long GetStyle(IntPtr hwnd)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64()
            : GetWindowLong32(hwnd, GWL_STYLE);

    private static void SetStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(style));
        else SetWindowLong32(hwnd, GWL_STYLE, (int)style);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
