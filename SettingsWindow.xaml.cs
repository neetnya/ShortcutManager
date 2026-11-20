using System.Windows;
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
    private bool _syncingModifiers;

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

        CloseToTrayRadio.IsChecked = _vm.CloseToTray;
        CloseToExitRadio.IsChecked = !_vm.CloseToTray;
        CloseToTrayRadio.Checked += (_, _) => _vm.CloseToTray = true;
        CloseToExitRadio.Checked += (_, _) => _vm.CloseToTray = false;

        SyncModifierCheckboxes();
        ModAlt.Checked += OnModifierChanged;
        ModAlt.Unchecked += OnModifierChanged;
        ModCtrl.Checked += OnModifierChanged;
        ModCtrl.Unchecked += OnModifierChanged;
        ModShift.Checked += OnModifierChanged;
        ModShift.Unchecked += OnModifierChanged;
        ModWin.Checked += OnModifierChanged;
        ModWin.Unchecked += OnModifierChanged;

        HotkeyKeyBox.Text = KeyToString(_vm.Key);
    }

    private void OnModifierChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingModifiers) return;
        UpdateModifiers();
    }

    private void UpdateModifiers()
    {
        var mods = 0;
        if (ModAlt.IsChecked == true) mods |= (int)HotkeyModifiers.Alt;
        if (ModCtrl.IsChecked == true) mods |= (int)HotkeyModifiers.Ctrl;
        if (ModShift.IsChecked == true) mods |= (int)HotkeyModifiers.Shift;
        if (ModWin.IsChecked == true) mods |= (int)HotkeyModifiers.Win;

        var before = _vm.Modifiers;
        _vm.Modifiers = mods;
        if (_vm.Modifiers == before && mods == 0)
        {
            SyncModifierCheckboxes();
            return;
        }

        TryRegister();
    }

    private void SyncModifierCheckboxes()
    {
        _syncingModifiers = true;
        try
        {
            ModAlt.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Alt) != 0;
            ModCtrl.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Ctrl) != 0;
            ModShift.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Shift) != 0;
            ModWin.IsChecked = (_vm.Modifiers & (int)HotkeyModifiers.Win) != 0;
        }
        finally
        {
            _syncingModifiers = false;
        }
    }

    private void HotkeyKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 先解包 Key.System（按住 Alt 或按 F10 时真实按键在 e.SystemKey）
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 忽略纯修饰键（Alt/Ctrl/Shift/Win），只接受可作主键的键
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            e.Handled = true;
            return;
        }

        var vk = KeyToVirtualKey(key);
        if (vk <= 0)
        {
            e.Handled = true;
            return;
        }

        _vm.Key = vk;
        HotkeyKeyBox.Text = KeyToString(key);
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
        _vm.Commit();
        Close();
    }

    /// <summary>给 App 层读取最终生效的设置（与主窗口共用同一对象，避免覆盖窗口位置等字段）。</summary>
    public AppSettings AppliedSettings => _vm.Commit();

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
