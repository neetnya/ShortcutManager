using System.Drawing;
using System.Windows.Forms;

namespace ShortcutManager;

/// <summary>
/// 托盘图标的最小封装（只借用 WinForms 的 NotifyIcon，不引入任何 WinForms 窗体）。
/// 单击（左键/中键）= 唤出主窗口；右键菜单 = 显示主窗口 / 设置 / 退出。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private NotifyIcon? _icon;
    private Icon? _ownedAppIcon;
    private ContextMenuStrip? _menu;
    private bool _disposed;

    /// <summary>单击托盘图标（或点菜单“显示主窗口”）时触发。</summary>
    public event Action? Activated;

    /// <summary>点菜单“设置”时触发。</summary>
    public event Action? SettingsRequested;

    /// <summary>点菜单“退出”时触发。</summary>
    public event Action? ExitRequested;

    public bool IsVisible => _icon is { Visible: true };

    /// <summary>创建并显示托盘图标（重复调用无副作用）。</summary>
    public void Show()
    {
        if (_disposed) return;

        if (_icon is null) Create();
        if (_icon is null) return;

        try { _icon.Visible = true; } catch { /* ignore */ }
    }

    /// <summary>第一次收进托盘时的气泡说明。失败则静默（策略禁用通知时不影响使用）。</summary>
    public void ShowInfoBalloon(string title, string text)
    {
        if (_icon is null) return;
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
        }
        catch { /* ignore */ }
    }

    /// <summary>临时隐藏（退出前用；也可再次 Show 回来）。</summary>
    public void Hide()
    {
        try { if (_icon is not null) _icon.Visible = false; } catch { /* ignore */ }
    }

    private void Create()
    {
        try
        {
            try { Application.EnableVisualStyles(); } catch { /* ignore */ }

            var open = new ToolStripMenuItem("显示主窗口");
            open.Click += (_, _) => Activated?.Invoke();

            var settings = new ToolStripMenuItem("设置");
            settings.Click += (_, _) => SettingsRequested?.Invoke();

            var exit = new ToolStripMenuItem("退出");
            exit.Click += (_, _) => ExitRequested?.Invoke();

            _menu = new ContextMenuStrip();
            _menu.Items.Add(open);
            _menu.Items.Add(settings);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exit);

            _ownedAppIcon = TryExtractAppIcon();
            _icon = new NotifyIcon
            {
                Icon = _ownedAppIcon ?? SystemIcons.Application,
                Text = "快捷方式管理器",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.MouseClick += OnMouseClick;
        }
        catch
        {
            _icon = null;
            try { _ownedAppIcon?.Dispose(); } catch { /* ignore */ }
            _ownedAppIcon = null;
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            Activated?.Invoke();
    }

    /// <summary>仅供自动化验证（`--traytest`）用。</summary>
    internal void SimulateClick(MouseButtons button)
        => OnMouseClick(this, new MouseEventArgs(button, clicks: 1, x: 0, y: 0, delta: 0));

    private static Icon? TryExtractAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                return Icon.ExtractAssociatedIcon(exe);
        }
        catch { /* ignore */ }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); } } catch { /* ignore */ }
        try { _ownedAppIcon?.Dispose(); } catch { /* ignore */ }
        try { _menu?.Dispose(); } catch { /* ignore */ }

        _icon = null;
        _ownedAppIcon = null;
        _menu = null;
    }
}
