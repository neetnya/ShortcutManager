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
}
