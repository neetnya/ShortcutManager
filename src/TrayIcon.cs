using System.Drawing;
using System.Windows.Forms;

namespace ShortcutManager;

/// <summary>
/// 托盘图标的最小封装（只借用 WinForms 的 NotifyIcon，不引入任何 WinForms 窗体）。
/// 单击（左键/中键）= 唤出主窗口；右键菜单 = 显示主窗口 / 退出。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private NotifyIcon? _icon;
    private ContextMenuStrip? _menu;
    private bool _disposed;

    /// <summary>单击托盘图标（或点菜单“显示主窗口”）时触发。</summary>
    public event Action? Activated;

    /// <summary>点菜单“退出”时触发。</summary>
    public event Action? ExitRequested;

    public bool IsVisible => _icon is { Visible: true };

    /// <summary>创建并显示托盘图标（重复调用无副作用）。</summary>
    public void Show()
    {
        if (_disposed) return;

        if (_icon is null) Create();
        if (_icon is null) return;   // 创建失败：安静降级，不影响窗口本身

        try { _icon.Visible = true; } catch { /* ignore */ }
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
            // 未初始化视觉样式时 ContextMenuStrip 会是 Win95 灰菜单，这里先打开（必须在创建控件之前）
            try { Application.EnableVisualStyles(); } catch { /* ignore */ }

            var open = new ToolStripMenuItem("显示主窗口");
            open.Click += (_, _) => Activated?.Invoke();

            var exit = new ToolStripMenuItem("退出");
            exit.Click += (_, _) => ExitRequested?.Invoke();

            _menu = new ContextMenuStrip();
            _menu.Items.Add(open);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exit);

            _icon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "快捷方式管理器",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            // 单击左键即唤出（不注册 DoubleClick：那样双击会连着触发两次）
            _icon.MouseClick += OnMouseClick;
        }
        catch
        {
            // 托盘不可用（例如被策略禁用）时不做任何事，窗口功能照旧
            _icon = null;
        }
    }

    /// <summary>
    /// 托盘鼠标点击的唯一入口。只有左键（以及习惯性的中键）唤出窗口；
    /// 右键留给 ContextMenuStrip 弹菜单，不做任何窗口操作。
    /// </summary>
    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            Activated?.Invoke();
    }

    /// <summary>
    /// 仅供自动化验证（`--traytest`）：用与真实点击**完全相同**的处理链模拟一次托盘点击，
    /// 因此也能覆盖“右键不该恢复窗口”这条规则。
    /// </summary>
    internal void SimulateClick(MouseButtons button)
        => OnMouseClick(this, new MouseEventArgs(button, clicks: 1, x: 0, y: 0, delta: 0));

    /// <summary>取 exe 里嵌的图标（csproj 的 ApplicationIcon）；失败退回系统默认图标。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { /* ignore */ }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先摘图标再释放：否则托盘里会留一个点一下才消失的“幽灵图标”
        try { if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); } } catch { /* ignore */ }
        try { _menu?.Dispose(); } catch { /* ignore */ }

        _icon = null;
        _menu = null;
    }
}
