using ShortcutManager.Models;
using ShortcutManager.Services;

namespace ShortcutManager.ViewModels;

/// <summary>设置窗口的状态，直接读写 <see cref="AppSettings"/>，改动即时落盘。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _source;

    private bool _closeToTray;
    private bool _autoStart;
    private bool _startMinimized;
    private bool _alwaysOnTop;
    private bool _keepFrontAfterOpen;
    private int _modifiers;
    private int _key;

    public SettingsViewModel(SettingsStore store, AppSettings settings)
    {
        _store = store;
        settings.Normalize();
        _source = settings;
        _closeToTray = settings.CloseToTray;
        _autoStart = settings.AutoStart;
        _startMinimized = settings.StartMinimized;
        _alwaysOnTop = settings.AlwaysOnTop;
        _keepFrontAfterOpen = settings.KeepFrontAfterOpen;
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

    /// <summary>窗口始终置顶（钉住）。</summary>
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set { if (Set(ref _alwaysOnTop, value)) Save(); }
    }

    /// <summary>打开条目后窗口短暂置顶，方便连续打开多个。</summary>
    public bool KeepFrontAfterOpen
    {
        get => _keepFrontAfterOpen;
        set { if (Set(ref _keepFrontAfterOpen, value)) Save(); }
    }

    public int Modifiers
    {
        get => _modifiers;
        set
        {
            var valid = (int)(HotkeyModifiers.Alt | HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Win);
            var next = value & valid;
            if (next == 0)
            {
                StatusText = "快捷键至少需要一个修饰键（Alt / Ctrl / Shift / Win）";
                return;
            }
            if (Set(ref _modifiers, next)) Save();
        }
    }

    public int Key
    {
        get => _key;
        set { if (Set(ref _key, value)) Save(); }
    }

    /// <summary>把界面字段写回目标对象；窗口位置、托盘提示等未在设置页展示的字段保持原样。</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.CloseToTray = _closeToTray;
        settings.AutoStart = _autoStart;
        settings.StartMinimized = _startMinimized;
        settings.AlwaysOnTop = _alwaysOnTop;
        settings.KeepFrontAfterOpen = _keepFrontAfterOpen;
        settings.HotkeyModifiers = _modifiers;
        settings.HotkeyKey = _key;
        if (ReferenceEquals(settings, _source)) return;
        settings.WindowLeft = _source.WindowLeft;
        settings.WindowTop = _source.WindowTop;
        settings.WindowWidth = _source.WindowWidth;
        settings.WindowHeight = _source.WindowHeight;
        settings.HasShownTrayHint = _source.HasShownTrayHint;
    }

    /// <summary>写回并返回正在使用的那份设置（主窗口和 App 共用同一对象）。</summary>
    public AppSettings Commit()
    {
        ApplyTo(_source);
        return _source;
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private void Save()
    {
        ApplyTo(_source);
        try { _store.Save(_source); }
        catch { StatusText = "设置保存失败"; }
    }
}
