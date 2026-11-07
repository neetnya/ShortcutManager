using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ShortcutManager;

/// <summary>可选的窗口视觉效果。全部为尽力而为，失败不影响功能。</summary>
internal static class WindowEffects
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 只做 Windows 11 圆角。
    /// 不使用亚克力（SetWindowCompositionAttribute）：第三方背景模糊会让整窗内容发灰发虚，
    /// 这类小工具窗口要的是内容清晰、启动快，所以保持不透明背景。
    /// </summary>
    public static void ApplyAcrylic(Window window)
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
    /// 禁用最大化：窗口固定尺寸，最大化按钮、标题栏双击、系统菜单里的“最大化”都不再有效。
    /// <para>
    /// WPF 没有“保留拖拽改变大小、但不要最大化”的开关（`ResizeMode` 要么连最大化按钮一起带上，
    /// 要么把边框和缩放一起去掉），所以直接改 Win32 窗口样式，再去消息层补漏。
    /// 三层保险：样式位 → 拦 SC_MAXIMIZE/双击标题栏 → 万一还是进了最大化就立刻还原。
    /// </para>
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

        try
        {
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int msg, IntPtr w, IntPtr _, ref bool handled) =>
            {
                switch (msg)
                {
                    // 系统菜单 / Win+↑ 走的是 SC_MAXIMIZE
                    case WM_SYSCOMMAND when ((int)w & 0xFFF0) == SC_MAXIMIZE:
                        handled = true;
                        return IntPtr.Zero;

                    // 双击标题栏（样式位已经能挡，这里再兜一次）
                    case WM_NCLBUTTONDBLCLK when (int)w == HTCAPTION:
                        handled = true;
                        return IntPtr.Zero;
                }
                return IntPtr.Zero;
            });
        }
        catch { /* ignore */ }

        // 最后一道保险：任何途径（Aero Snap 等）真进了最大化，立刻退回普通状态
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Maximized)
                window.WindowState = WindowState.Normal;
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

    // 32/64 位各一套：user32 里 GetWindowLongPtrW 只在 64 位系统上存在
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
