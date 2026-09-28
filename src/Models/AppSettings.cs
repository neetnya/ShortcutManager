namespace ShortcutManager.Models;
/// <summary>
/// 应用设置（存于 exe 同目录的 settings.json，与 config.json 分离）。
/// 全部为可选字段并带默认值，任何一项缺失或文件损坏都回退到默认，不影响使用。
/// </summary>
public sealed class AppSettings
{
    /// <summary>关闭按钮行为：true = 关闭即最小化到托盘，false = 关闭即退出软件。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>全局唤起快捷键的修饰键（组合值，见 <see cref="HotkeyModifiers"/>）。默认 Alt。</summary>
    public int HotkeyModifiers { get; set; } = (int)ShortcutManager.Models.HotkeyModifiers.Alt;

    /// <summary>全局唤起快捷键的虚拟键码（默认 Q）。</summary>
    public int HotkeyKey { get; set; } = 0x51;

    /// <summary>开机自启。</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>启动后最小化到托盘（后台运行），不弹出主窗口。</summary>
    public bool StartMinimized { get; set; } = false;
}

/// <summary>全局快捷键的修饰键标志（与 Win32 RegisterHotKey 的 fsModifiers 对齐）。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0x0000,
    Alt = 0x0001,
    Ctrl = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}
