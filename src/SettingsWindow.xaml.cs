using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShortcutManager.Models;
using ShortcutManager.Services;
using ShortcutManager.ViewModels;

namespace ShortcutManager;

/// <summary>设置窗口：关闭行为 / 全局快捷键 / 开机自启 / 启动最小化。</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly GlobalHotkeyManager _hotkeys;
    private bool _loaded;

    public SettingsWindow(SettingsViewModel vm, GlobalHotkeyManager hotkeys)
    {
        _vm = vm;
        _hotkeys = hotkeys;
        DataContext = vm;
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        // 关闭行为
        CloseToTrayRadio.IsChecked = _vm.CloseToTray;
        CloseToExitRadio.IsChecked = !_vm.CloseToTray;
        CloseToTrayRadio.Checked += (_, _) => _vm.CloseToTray = true;
        CloseToExitRadio.Checked += (_, _) => _vm.CloseToTray = false;

        // 快捷键修饰键
        ModAlt.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Alt) != 0;
        ModCtrl.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Ctrl) != 0;
        ModShift.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Shift) != 0;
        ModWin.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Win) != 0;

        ModAlt.Checked += (_, _) => UpdateModifiers();
        ModAlt.Unchecked += (_, _) => UpdateModifiers();
        ModCtrl.Checked += (_, _) => UpdateModifiers();
        ModCtrl.Unchecked += (_, _) => UpdateModifiers();
        ModShift.Checked += (_, _) => UpdateModifiers();
        ModShift.Unchecked += (_, _) => UpdateModifiers();
        ModWin.Checked += (_, _) => UpdateModifiers();
        ModWin.Unchecked += (_, _) => UpdateModifiers();

        HotkeyKeyBox.Text = KeyToString(_vm.Key);

        // 其余开关
        AutoStartCheck.IsChecked = _vm.AutoStart;
        StartMinimizedCheck.IsChecked = _vm.StartMinimized;
        AutoStartCheck.Checked += (_, _) => _vm.AutoStart = true;
        AutoStartCheck.Unchecked += (_, _) => _vm.AutoStart = false;
        StartMinimizedCheck.Checked += (_, _) => _vm.StartMinimized = true;
        StartMinimizedCheck.Unchecked += (_, _) => _vm.StartMinimized = false;
    }

    private void UpdateModifiers()
    {
        var mods = 0;
        if (ModAlt.IsChecked == true) mods |= (int)HotkeyModifiers.Alt;
        if (ModCtrl.IsChecked == true) mods |= (int)HotkeyModifiers.Ctrl;
        if (ModShift.IsChecked == true) mods |= (int)HotkeyModifiers.Shift;
        if (ModWin.IsChecked == true) mods |= (int)HotkeyModifiers.Win;
        _vm.Modifiers = mods;
        TryRegister();
    }

    private void HotkeyKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 捕获主键：忽略纯修饰键（Alt/Ctrl/Shift/Win），只接受可作主键的键
        if (e.Key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var vk = KeyToVirtualKey(e.Key);
        if (vk <= 0)
        {
            e.Handled = true;
            return;
        }

        _vm.Key = vk;
        HotkeyKeyBox.Text = KeyToString(e.Key == Key.System ? e.SystemKey : e.Key);
        e.Handled = true;
        TryRegister();
    }

    /// <summary>改动后即时重新注册，并提示冲突。</summary>
    private void TryRegister()
    {
        var ok = _hotkeys.TryRegister((uint)_vm.Modifiers, (uint)_vm.Key);
        _vm.StatusText = ok
            ? ""
            : "该快捷键可能被其他程序占用，无法注册（不影响软件使用）";
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        // 关闭前确保快捷键以最终值重新注册（失败也保持静默降级）
        _vm.ApplyTo(_appliedSettings);
        Close();
    }

    private AppSettings _appliedSettings = new();

    /// <summary>给 App 层读取最终生效的设置（保存后返回）。</summary>
    public AppSettings AppliedSettings
    {
        get
        {
            _vm.ApplyTo(_appliedSettings);
            return _appliedSettings;
        }
    }

    // ---- 键码/字符串转换 ----

    private static int KeyToVirtualKey(Key key)
    {
        try { return KeyInterop.VirtualKeyFromKey(key); }
        catch { return 0; }
    }

    private static string KeyToString(Key key)
        => key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Left => "←",
            Key.Right => "→",
            Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
            Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
            Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
            _ => key.ToString(),
        };

    private static string KeyToString(int vk)
        => KeyToString(KeyInterop.KeyFromVirtualKey(vk));
}
