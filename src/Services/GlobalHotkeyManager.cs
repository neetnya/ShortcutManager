using System.Runtime.InteropServices;
using System.Windows.Interop;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>
/// 全局快捷键：用 Win32 RegisterHotKey 注册，程序在后台（窗口隐藏/在托盘）时也能响应。
/// 通过主窗口的 HwndSource 消息钩子接收 WM_HOTKEY。
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x5A4D; // 固定一个 id 即可，每次只注册一个快捷键

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;

    /// <summary>快捷键被按下时触发（已在 UI 线程）。</summary>
    public event Action? Activated;

    /// <summary>是否成功注册。false 时快捷键不可用（被占用或注册失败），软件其余功能照常。</summary>
    public bool IsRegistered => _registered;

    /// <summary>挂到指定窗口的消息循环上，并注册快捷键。</summary>
    public void Attach(System.Windows.Window window, AppSettings settings)
    {
        Detach();
        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero) return;

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        Register((uint)settings.HotkeyModifiers, (uint)settings.HotkeyKey);
    }

    /// <summary>重新注册（用户改了快捷键后调用）。</summary>
    public void Reregister(AppSettings settings)
    {
        Register((uint)settings.HotkeyModifiers, (uint)settings.HotkeyKey);
    }

    /// <summary>尝试注册；成功与否只影响快捷键是否可用，不抛异常。</summary>
    public bool TryRegister(uint modifiers, uint vk)
    {
        Unregister();
        return Register(modifiers, vk);
    }

    private bool Register(uint modifiers, uint vk)
    {
        Unregister();
        if (_hwnd == IntPtr.Zero || vk == 0) { _registered = false; return false; }

        _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers, vk);
        return _registered;
    }

    private void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            try { UnregisterHotKey(_hwnd, HotkeyId); } catch { /* ignore */ }
        }
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Activated?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>仅自动化验证（--hotkeytest）用：直接走 WM_HOTKEY 的处理链，确认 hook 逻辑无误。</summary>
    internal void SimulateHotkey()
    {
        var handled = false;
        WndProc(_hwnd, WM_HOTKEY, new IntPtr(HotkeyId), IntPtr.Zero, ref handled);
    }

    private void Detach()
    {
        Unregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _hwnd = IntPtr.Zero;
    }

    public void Dispose() => Detach();
}
