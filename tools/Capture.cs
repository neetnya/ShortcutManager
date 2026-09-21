using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public class Nat
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

    // PrintWindow 让窗口直接把自己画到我们的 DC 上，绕开屏幕坐标与 DPI 虚拟化问题
    [DllImport("user32.dll", EntryPoint = "PrintWindow")]
    private static extern bool PrintWindowNative(IntPtr hwnd, IntPtr hdc, uint flags);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public static bool Capture(IntPtr hwnd, int w, int h, string path)
    {
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                bool ok = Draw(hwnd, g, PW_RENDERFULLCONTENT);
                if (!ok) ok = Draw(hwnd, g, 0);
                bmp.Save(path, ImageFormat.Png);
                return ok;
            }
        }
    }

    private static bool Draw(IntPtr hwnd, Graphics g, uint flags)
    {
        IntPtr hdc = g.GetHdc();
        try { return PrintWindowNative(hwnd, hdc, flags); }
        finally { g.ReleaseHdc(hdc); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] private static extern IntPtr GetDpiForSystem();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);

    /// <summary>0=unaware 1=system 2=per-monitor</summary>
    public static int DpiAwareness() { return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()); }
    public static int SystemDpi() { return (int)GetDpiForSystem(); }

    /// <summary>
    /// 窗口真实物理像素尺寸，格式 "宽,高,dpi"。
    /// 返回字符串而不是数组：PowerShell 会自动展开 .NET 数组，导致取元素时拿到 null。
    /// 调用方若不是 DPI 感知进程，GetWindowRect 会返回被缩放过的“虚拟”坐标，
    /// 因此用窗口 DPI 反算出真实像素。
    /// </summary>
    public static string PhysicalSize(IntPtr hwnd)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return "0,0,96";

        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;

        int w = (int)Math.Round((r.R - r.L) * dpi / 96.0);
        int h = (int)Math.Round((r.B - r.T) * dpi / 96.0);
        return w + "," + h + "," + dpi;
    }

    // ==================================================================
    //  窗口查找
    //  WPF 会创建多个 HwndWrapper* 辅助窗口（隐藏、无标题），
    //  必须挑“有标题”的那个才是主窗口，否则会把隐藏的辅助窗口误判为主窗口。
    //  用静态字段收集结果：PowerShell 的 ScriptBlock 委托里访问 $script: 作用域不可靠。
    // ==================================================================

    private static readonly System.Collections.Generic.List<string> Found =
        new System.Collections.Generic.List<string>();
    private static uint _targetPid;
    private static IntPtr _mainHwnd;

    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);

    private static bool Callback(IntPtr h, IntPtr p)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != _targetPid) return true;

        var cls = new System.Text.StringBuilder(256);
        GetClassName(h, cls, 256);
        var c = cls.ToString();
        if (!c.StartsWith("HwndWrapper") || c.Contains("BroadcastEvent")) return true;

        var txt = new System.Text.StringBuilder(256);
        GetWindowText(h, txt, 256);

        Found.Add("hwnd=" + h + " visible=" + IsWindowVisible(h) + " title='" + txt + "'");
        if (txt.Length > 0 && _mainHwnd == IntPtr.Zero) _mainHwnd = h;
        return true;
    }

    /// <summary>扫描指定进程的窗口，返回诊断文本；主窗口 hwnd 用 MainHwnd 取。</summary>
    public static string ScanWindows(int pid)
    {
        Found.Clear();
        _mainHwnd = IntPtr.Zero;
        _targetPid = (uint)pid;
        EnumWindows(Callback, IntPtr.Zero);
        return string.Join("\n", Found);
    }

    /// <summary>
    /// 主窗口句柄，以 Int64 返回。
    /// 不用 IntPtr：PowerShell 把 IntPtr 从属性/哈希表里取出来时会变成 0。
    /// </summary>
    public static long MainHwndLong { get { return _mainHwnd.ToInt64(); } }
}
