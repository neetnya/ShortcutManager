using ShortcutManager.Models;
using ShortcutManager.Services;

namespace ShortcutManager.ViewModels;

/// <summary>设置窗口的状态，直接读写 <see cref="AppSettings"/>，改动即时落盘。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    private bool _closeToTray;
    private bool _autoStart;
    private bool _startMinimized;
    private int _modifiers;
    private int _key;

    public SettingsViewModel(SettingsStore store, AppSettings settings)
    {
        _store = store;
        _closeToTray = settings.CloseToTray;
        _autoStart = settings.AutoStart;
        _startMinimized = settings.StartMinimized;
        _modifiers = settings.HotkeyModifiers;
        _key = settings.HotkeyKey;
    }

    /// <summary>关闭按钮行为：true = 最小化到托盘，false = 退出软件。</summary>
    public bool CloseToTray
    {
        get => _closeToTray;
        set { if (Set(ref _closeToTray, value)) Save(); }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value)) return;
            if (!AutoStartService.SetEnabled(value))
                StatusText = value ? "设置开机自启失败" : "取消开机自启失败";
            else
                StatusText = value ? "已开启开机自启" : "已关闭开机自启";
            Save();
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set { if (Set(ref _startMinimized, value)) Save(); }
    }

    public int Modifiers
    {
        get => _modifiers;
        set { if (Set(ref _modifiers, value)) Save(); }
    }

    public int Key
    {
        get => _key;
        set { if (Set(ref _key, value)) Save(); }
    }

    /// <summary>把当前快捷键字段同步回 AppSettings（供外部读取后重新注册）。</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.CloseToTray = _closeToTray;
        settings.AutoStart = _autoStart;
        settings.StartMinimized = _startMinimized;
        settings.HotkeyModifiers = _modifiers;
        settings.HotkeyKey = _key;
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private void Save()
    {
        var settings = new AppSettings();
        ApplyTo(settings);
        try { _store.Save(settings); }
        catch { StatusText = "设置保存失败"; }
    }
}
