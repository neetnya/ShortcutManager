using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ShortcutManager.Models;
using ShortcutManager.Services;
using ShortcutManager.ViewModels;

namespace ShortcutManager;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand MinimizeCommand = new();
    public static readonly RoutedCommand NewGroupCommand = new();
    public static readonly RoutedCommand DeleteCommand = new();
    public static readonly RoutedCommand RenameCommand = new();
    public static readonly RoutedCommand QuitCommand = new();

    private Point _dragStart;
    private ShortcutViewModel? _dragCandidate;
    private ShortcutViewModel? _dragSource;
    private ShortcutViewModel? _menuTarget;
    private bool _insertAfter;

    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        CommandBindings.Add(new CommandBinding(MinimizeCommand, (_, _) => WindowState = WindowState.Minimized));
        CommandBindings.Add(new CommandBinding(NewGroupCommand, (_, _) => CreateGroup()));
        CommandBindings.Add(new CommandBinding(DeleteCommand, (_, _) => RemoveSelectedItems()));
        CommandBinding renameBinding = new(RenameCommand, (_, _) =>
        {
            if (ViewModel.SelectedItem is { } item) BeginItemRename(item);
        });
        CommandBindings.Add(renameBinding);
        CommandBindings.Add(new CommandBinding(QuitCommand, (_, _) => Close()));

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        AllowDrop = true;
        Drop += Window_Drop;
        DragOver += Window_DragOver;

        // 标签栏翻页箭头的显隐/可用状态跟着布局与滚动走
        TabScroll.ScrollChanged += (_, _) => UpdateTabArrows();
        TabScroll.SizeChanged += (_, _) => UpdateTabArrows();
        TabList.LayoutUpdated += (_, _) => UpdateTabArrows();
        TabList.SelectionChanged += TabList_SelectionChanged;

        // 调试定位用：--import <路径> 启动时直接把路径导入当前分组
        var args = Environment.GetCommandLineArgs();
        var idx = Array.FindIndex(args, a => string.Equals(a, "--import", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                ViewModel.Import(new[] { args[idx + 1] })));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 预热当前分组图标，避免首屏图标逐个跳出
        if (ViewModel.SelectedGroup is { } group)
            ViewModel.WarmIcons(group);

        // ---- 诊断/自动化截图模式（仅用于开发验证，不影响正常使用）----
        var args = Environment.GetCommandLineArgs();

        if (args.Any(a => a == "--diag"))
        {
            var src = PresentationSource.FromVisual(this);
            var m = src?.CompositionTarget?.TransformToDevice;
            var lines = new[]
            {
                $"Width(DIP)={Width} Height(DIP)={Height}",
                $"ActualWidth(DIP)={ActualWidth} ActualHeight(DIP)={ActualHeight}",
                $"TransformToDevice={m}",
                $"TileList.ActualWidth={TileList.ActualWidth} TileList.ActualHeight={TileList.ActualHeight}",
                $"TabList.ActualWidth={TabList.ActualWidth}",
                $"TabScroll.ScrollableWidth={TabScroll.ScrollableWidth:0.0} TabArrows={TabArrows.Visibility}",
                $"maximizeBox={WindowEffects.HasMaximizeBox(this)}",
                $"Groups={ViewModel.Groups.Count} Items={ViewModel.SelectedItems?.Count}",
            };
            TryWriteLog("diag.log", lines);
        }

        // ---- 界面行为验证：最大化已禁用 / 标签栏箭头 / 多选删除，结果写入 ui-test.log ----
        if (args.Any(a => a == "--uitest"))
        {
            _uiLog = new List<string>();
            ViewModel.SuppressSave = true;   // 测试会真的增删分组和条目，别写进真实配置
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                await Task.Delay(700);
                var ok = true;
                string Pct(double v) => v.ToString("0.0");

                // 1) 最大化按钮已从窗口样式里去掉
                var noBox = !WindowEffects.HasMaximizeBox(this);
                UiTestLog($"expect-no-maximizebox(WS_MAXIMIZEBOX 已移除)={noBox}");
                ok &= noBox;

                // 2) 直接要求最大化 → 应被拒绝并还原
                WindowState = WindowState.Maximized;
                await Task.Delay(300);
                var rejected = WindowState != WindowState.Maximized;
                UiTestLog($"expect-maximize-rejected(设 Maximized 后被拉回)={rejected} state={WindowState}");
                ok &= rejected;
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                await Task.Delay(250);

                // 3) 分组少 → 不显示翻页箭头
                while (ViewModel.Groups.Count > 2) ViewModel.RemoveGroup(ViewModel.Groups[^1]);
                await Task.Delay(300);
                UpdateTabArrows();
                var few = TabArrows.Visibility == Visibility.Collapsed && TabScroll.ScrollableWidth <= 0.5;
                UiTestLog($"few-groups: groups={ViewModel.Groups.Count} scrollable={Pct(TabScroll.ScrollableWidth)} " +
                          $"arrows={TabArrows.Visibility} => 不显示={few}");
                ok &= few;

                // 4) 分组多到放不下 → 出现箭头；回到第一个标签时只有“向右”可点
                while (ViewModel.Groups.Count < 14) ViewModel.AddGroup();
                ViewModel.SelectedGroup = ViewModel.Groups[0];   // 选中态会把标签滚进视野，这里先把偏移归零
                await Task.Delay(500);
                ScrollTabs(-9999);
                UpdateTabArrows();
                var many = TabArrows.Visibility == Visibility.Visible
                           && TabScroll.ScrollableWidth > 0.5
                           && !TabLeftButton.IsEnabled && TabRightButton.IsEnabled;
                UiTestLog($"many-groups: groups={ViewModel.Groups.Count} scrollable={Pct(TabScroll.ScrollableWidth)} " +
                          $"offset={Pct(TabScroll.HorizontalOffset)} arrows={TabArrows.Visibility} " +
                          $"left={TabLeftButton.IsEnabled} right={TabRightButton.IsEnabled} => 显示且方向可用={many}");
                ok &= many;

                // 5) 点箭头能滚；滚到最右后右箭头禁用、左箭头可用
                TabRightButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Task.Delay(250);
                var scrolled = TabScroll.HorizontalOffset > 0.5;
                UiTestLog($"expect-arrow-scrolls(点右箭头后偏移变大)={scrolled} offset={Pct(TabScroll.HorizontalOffset)}");
                ok &= scrolled;

                ScrollTabs(9999);
                await Task.Delay(250);
                UpdateTabArrows();
                var atEnd = TabScroll.HorizontalOffset >= TabScroll.ScrollableWidth - 0.5
                            && !TabRightButton.IsEnabled && TabLeftButton.IsEnabled;
                UiTestLog($"expect-arrow-bounds(到底后右禁用/左可用)={atEnd} offset={Pct(TabScroll.HorizontalOffset)}");
                ok &= atEnd;

                // 6) 多选（Ctrl/Shift）→ 一次性移除，磁盘文件不动
                var tmp = Path.Combine(Path.GetTempPath(), "sm-uitest");
                Directory.CreateDirectory(tmp);
                var files = new List<string>();
                for (var i = 0; i < 3; i++)
                {
                    var f = Path.Combine(tmp, $"t{i}.txt");
                    File.WriteAllText(f, "x");
                    files.Add(f);
                }
                ViewModel.Import(files);
                await Task.Delay(250);

                var group = ViewModel.SelectedGroup!;
                var before = group.Items.Count;
                TileList.SelectedItems.Clear();
                foreach (var it in group.Items.Skip(Math.Max(0, before - 3))) TileList.SelectedItems.Add(it);
                var selected = SelectedTiles().Count;
                RemoveSelectedItems();
                await Task.Delay(250);
                var removed = before - group.Items.Count;

                var multi = TileList.SelectionMode == SelectionMode.Extended && selected == 3 && removed == 3;
                UiTestLog($"expect-multi-delete(mode={TileList.SelectionMode} 选中{selected} 移除{removed})={multi}");
                ok &= multi;
                UiTestLog($"keep-files(磁盘文件未被删除)={files.All(File.Exists)}");
                ok &= files.All(File.Exists);

                try { Directory.Delete(tmp, true); } catch { /* ignore */ }

                UiTestLog($"result={(ok ? "PASS" : "FAIL")}");
                Environment.ExitCode = ok ? 0 : 1;
                Close();   // SuppressSave 仍为 true：关闭时不会把测试数据写回配置
            }));
        }

        var shotIndex = Array.FindIndex(args, a => a == "--shot");
        if (shotIndex >= 0 && shotIndex + 1 < args.Length)
        {
            var outPath = args[shotIndex + 1];
            // 等一帧渲染完成后再抓图
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
                {
                    await Task.Delay(900);
                    CaptureToFile(outPath);
                    Application.Current.Shutdown();
                }));
            }));
        }

        // ---- 托盘行为验证：最小化→托盘→恢复→关闭，结果写入 tray-test.log ----
        if (args.Any(a => a == "--traytest"))
        {
            _trayLog = new List<string>();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                await Task.Delay(700);

                var ok = true;
                TrayTestLog($"start: visible={IsVisible} inTaskbar={ShowInTaskbar} state={WindowState}");
                ok &= IsVisible && ShowInTaskbar && WindowState == WindowState.Normal;

                // 1) 最小化（点标题栏最小化按钮 / Esc / Ctrl+W 都走这条路径）
                WindowState = WindowState.Minimized;
                await Task.Delay(500);
                var toTray = !IsVisible && !ShowInTaskbar && _tray is { IsVisible: true };
                TrayTestLog($"expect-to-tray(窗口隐藏+无任务栏按钮+托盘图标存在)={toTray}");
                ok &= toTray;

                // 2) 恢复：走真实的托盘点击处理链
                //    2a. 右键应弹菜单、不动窗口（回归：别把右键也接成唤出）
                Tray.SimulateClick(System.Windows.Forms.MouseButtons.Right);
                await Task.Delay(250);
                var rightNoop = !IsVisible && !ShowInTaskbar;
                TrayTestLog($"expect-rightclick-noop(右键不恢复窗口)={rightNoop}");
                ok &= rightNoop;

                //    2b. 等一会儿再单击左键：这段时间留给外部脚本把前台抢走，
                //        这样才复现得出“别的程序正占着前台”的真实场景
                TrayTestLog($"wait-for-focus-steal(3000ms)：外部脚本可在此时激活别的窗口");
                await Task.Delay(3000);
                // 确认“前台确实被别的程序占着”这个前提是否成立（不成立就说明这条断言是宽松的）
                TrayTestLog($"before-restore: 前台进程={App.ForegroundOwnerName()}");

                Tray.SimulateClick(System.Windows.Forms.MouseButtons.Left);
                await Task.Delay(500);
                var hwnd = App.HandleOf(this);
                var back = IsVisible && ShowInTaskbar && WindowState == WindowState.Normal;
                TrayTestLog($"expect-restored(窗口可见+任务栏按钮+Normal)={back}");
                ok &= back;

                // 2c. Win32 层的真实状态：这两条才是用户看到的东西
                var notIconic = !App.IsMinimizedHwnd(this);
                TrayTestLog($"expect-not-minimized(Win32 IsIconic=false)={notIconic}");
                ok &= notIconic;

                var isForeground = App.IsForeground(this);
                TrayTestLog($"expect-foreground(Win32 前台=本窗口)={isForeground}");
                ok &= isForeground;

                // 3) 关闭 = 真退出：Close() 后进程结束，结论用退出码表达
                TrayTestLog($"result={(ok ? "PASS" : "FAIL")}");
                Environment.ExitCode = ok ? 0 : 1;
                Close();
            }));
        }
    }

    /// <summary>用 RenderTargetBitmap 渲染自身，完全避开屏幕坐标与 DPI 虚拟化问题。</summary>
    private void CaptureToFile(string path)
    {
        try
        {
            var w = (int)Math.Ceiling(ActualWidth);
            var h = (int)Math.Ceiling(ActualHeight);
            if (w <= 0 || h <= 0) { TryWriteLog("shot.log", new[] { $"bad size {w}x{h}" }); return; }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);

            var src = PresentationSource.FromVisual(this);
            var lines = new List<string>
            {
                $"saved={path}",
                $"size(DIP)={w}x{h}",
                $"transform={src?.CompositionTarget?.TransformToDevice}",
                $"groups={ViewModel.Groups.Count} items={ViewModel.SelectedItems?.Count}",
            };

            // 顺带记录每格图标来源，便于排查「图标显示成兜底图」这类问题
            if (ViewModel.SelectedItems is { } items)
            {
                foreach (var it in items)
                {
                    var img = it.Icon;
                    lines.Add($"icon '{it.Name}' path='{it.Path}' stored='{it.StoredPath}' exists={it.Exists} " +
                              $"image={(img is null ? "null" : $"{img.GetType().Name} {img.Width}x{img.Height}")} " +
                              $"builtInFallback={ShellIcons.IsBuiltInFallback(img, it.IsDirectory)}");
                }
            }

            TryWriteLog("shot.log", lines.ToArray());
        }
        catch (Exception ex)
        {
            TryWriteLog("shot.log", new[] { "FAIL " + ex });
        }
    }

    private static void TryWriteLog(string name, string[] lines)
    {
        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, name), lines); }
        catch { /* ignore */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyAcrylic(this);
        WindowEffects.DisableMaximize(this);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭就是关闭：保存配置后让窗口正常关闭，进程随之退出。
        // （ShutdownMode = OnMainWindowClose）
        _exiting = true;
        ViewModel.Save();
        _tray?.Dispose();   // 摘掉托盘图标，别在托盘里留幽灵图标
        _tray = null;
        TrayTestLog("closing: 关闭事件已触发，配置已保存，托盘图标已释放");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 优先取消正在进行的重命名，其次最小化窗口
            if (TryCancelAnyRename()) { e.Handled = true; return; }
            WindowState = WindowState.Minimized;
            e.Handled = true;
        }
    }

    // ==================================================================
    //  最小化到托盘 / 关闭即退出
    // ==================================================================

    private TrayIcon? _tray;
    private bool _inTray;
    private bool _exiting;

    private TrayIcon Tray
    {
        get
        {
            if (_tray is null)
            {
                _tray = new TrayIcon();
                _tray.Activated += RestoreFromTray;
                _tray.ExitRequested += ExitFromTray;
            }
            return _tray;
        }
    }

    /// <summary>最小化 → 收进托盘（任务栏按钮同时消失），程序继续在后台运行。</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) HideToTray();
    }

    private void HideToTray()
    {
        if (_inTray || _exiting) return;

        _inTray = true;
        Tray.Show();                // 第一次最小化时才创建托盘图标
        ShowInTaskbar = false;
        Hide();
        ViewModel.StatusText = "已最小化到托盘，单击托盘图标即可恢复";
        TrayTestLog($"minimized: visible={IsVisible} inTaskbar={ShowInTaskbar} " +
                    $"state={WindowState} tray={_tray?.IsVisible == true}");
    }

    /// <summary>
    /// 从托盘恢复窗口（单击托盘图标、重复启动 exe、点菜单“显示主窗口”都会走这里）。
    /// <para>
    /// 顺序很关键，**不能**在隐藏状态下先改 WindowState：那样只改了 WPF 的属性，
    /// Win32 窗口的显示状态没变，随后 Show() 会让它以“最小化”的样子出现
    /// （表现为点了托盘没反应、还得自己点任务栏按钮）。
    /// 所以先 Show() 出来，再置 Normal，最后交给 ForceForeground 兜底。
    /// </para>
    /// </summary>
    public void RestoreFromTray()
    {
        if (_exiting) return;

        _inTray = false;
        ShowInTaskbar = true;
        Show();
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        App.ForceForeground(this);

        TrayTestLog($"restored: visible={IsVisible} inTaskbar={ShowInTaskbar} state={WindowState} " +
                    $"active={IsActive} iconic={App.IsMinimizedHwnd(this)} foreground={App.IsForeground(this)}");
    }

    private void ExitFromTray()
    {
        _exiting = true;
        Close();   // 走正常关闭流程：存盘 + 释放托盘图标 + 退出进程
    }

    /// <summary>开发验证用：--traytest 把最小化/恢复/关闭的每一步状态写入 tray-test.log。</summary>
    private List<string>? _trayLog;

    private void TrayTestLog(string line)
    {
        if (_trayLog is null) return;
        _trayLog.Add(line);
        TryWriteLog("tray-test.log", _trayLog.ToArray());
    }

    /// <summary>开发验证用：--uitest 把界面行为断言写入 ui-test.log。</summary>
    private List<string>? _uiLog;

    private void UiTestLog(string line)
    {
        if (_uiLog is null) return;
        _uiLog.Add(line);
        TryWriteLog("ui-test.log", _uiLog.ToArray());
    }

    // ==================================================================
    //  分组 tab
    // ==================================================================

    private void NewGroup_Click(object sender, RoutedEventArgs e) => CreateGroup();

    private void CreateGroup()
    {
        ViewModel.AddGroup();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (ViewModel.SelectedGroup is { } g)
            {
                BringGroupIntoView(g);   // 分组多了以后，新标签可能在可视区外
                BeginGroupRename(g);
                ViewModel.StatusText = "已新建分组";
            }
        }));
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGroup is not { } group) return;
        if (group.Items.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"分组“{group.Name}”中还有 {group.Items.Count} 个快捷方式，删除分组不会删除磁盘上的文件。\n\n确定删除该分组吗？",
                "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }
        ViewModel.RemoveGroup(group);
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGroup is { } group) BeginGroupRename(group);
    }

    // ---- 标签栏翻页箭头（原生横向滚动条太丑，换成最右的两个三角）----

    private const double TabScrollStep = 120;

    /// <summary>判定“已经滚到头”的容差：布局舍入会留下 1px 左右的偏移，别因此把箭头点亮。</summary>
    private const double TabScrollEpsilon = 1.5;

    /// <summary>
    /// 分组放不下时（内容宽 &gt; 视口宽）才显示箭头；能继续滚的方向才可点。
    /// <para>
    /// 箭头显示后视口会变窄，但只会让内容“更放不下”，不会出现显隐抖动：
    /// 反过来只有当内容连同箭头都塞得下时才会隐藏，而那时已经不需要滚动了。
    /// </para>
    /// </summary>
    private void UpdateTabArrows()
    {
        if (TabArrows is null || TabLeftButton is null) return;

        var overflow = TabScroll.ScrollableWidth > TabScrollEpsilon;
        var wanted = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (TabArrows.Visibility != wanted) TabArrows.Visibility = wanted;
        if (!overflow) return;

        var canLeft = TabScroll.HorizontalOffset > TabScrollEpsilon;
        var canRight = TabScroll.HorizontalOffset < TabScroll.ScrollableWidth - TabScrollEpsilon;
        if (TabLeftButton.IsEnabled != canLeft) TabLeftButton.IsEnabled = canLeft;
        if (TabRightButton.IsEnabled != canRight) TabRightButton.IsEnabled = canRight;
    }

    private void TabLeft_Click(object sender, RoutedEventArgs e) => ScrollTabs(-TabScrollStep);

    private void TabRight_Click(object sender, RoutedEventArgs e) => ScrollTabs(+TabScrollStep);

    private void ScrollTabs(double delta)
    {
        var target = Math.Clamp(TabScroll.HorizontalOffset + delta, 0, TabScroll.ScrollableWidth);
        TabScroll.ScrollToHorizontalOffset(target);
        UpdateTabArrows();
    }

    /// <summary>把指定标签滚进可视区域（新建分组、排序落点、切换分组时会用到）。</summary>
    private void BringGroupIntoView(ShortcutGroupViewModel group)
    {
        if (TabList.ItemContainerGenerator.ContainerFromItem(group) is not ListBoxItem container) return;
        if (container.ActualWidth <= 0) return;

        double left;
        try { left = container.TransformToAncestor(TabList).Transform(new Point(0, 0)).X; }
        catch { return; }   // 还没来得及布局

        var right = left + container.ActualWidth;
        var offset = TabScroll.HorizontalOffset;
        var viewport = TabScroll.ViewportWidth;

        if (left < offset) TabScroll.ScrollToHorizontalOffset(left);
        else if (right > offset + viewport) TabScroll.ScrollToHorizontalOffset(right - viewport);
        UpdateTabArrows();
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedGroup is not { } group) return;
        // 容器要等布局完成才拿得到
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => BringGroupIntoView(group)));
    }

    // ---- 分组标签拖拽排序 ----

    private const string GroupDragFormat = "ShortcutManager.GroupTab";

    private Point _tabDragStart;
    private ShortcutGroupViewModel? _tabDragCandidate;
    private ShortcutGroupViewModel? _tabDragSource;
    private bool _tabInsertAfter;

    private ShortcutGroupViewModel? GroupFrom(object? source)
    {
        if (source is not DependencyObject d) return null;
        return FindAncestor<ListBoxItem>(d)?.DataContext as ShortcutGroupViewModel;
    }

    private void TabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(this);
        // 正在就地重命名时不参与拖拽
        _tabDragCandidate = GroupFrom(e.OriginalSource) is { IsRenaming: false } g ? g : null;
    }

    private void TabList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragCandidate is null) return;
        if (Keyboard.FocusedElement is TextBox) return;

        var delta = e.GetPosition(this) - _tabDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var group = _tabDragCandidate;
        _tabDragCandidate = null;

        var data = new DataObject(GroupDragFormat, group);
        _tabDragSource = group;
        try
        {
            DragDrop.DoDragDrop(TabList, data, DragDropEffects.Move);
        }
        finally
        {
            _tabDragSource = null;
            ClearGroupDragMarkers();
        }
    }

    private void TabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _tabDragCandidate = null;

    private void TabList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(GroupDragFormat) && _tabDragSource is not null)
        {
            e.Effects = DragDropEffects.Move;
            UpdateGroupDropMarker(e);
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // 文件拖到标签栏：不在这里处理，冒泡给窗口级处理器（导入当前分组）
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void TabList_DragLeave(object sender, DragEventArgs e) => ClearGroupDragMarkers();

    private void TabList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(GroupDragFormat)) return;   // 文件拖入交给窗口级处理器

        ClearGroupDragMarkers();
        e.Handled = true;

        if (e.Data.GetData(GroupDragFormat) is not ShortcutGroupViewModel source) return;

        var target = GroupFrom(e.OriginalSource);
        if (target is null)
        {
            if (ViewModel.MoveGroupToEnd(source))
            {
                ViewModel.StatusText = $"“{source.Name}”已移到最后";
                BringGroupIntoView(source);
            }
            return;
        }

        if (ViewModel.MoveGroupTo(source, target, _tabInsertAfter))
        {
            ViewModel.StatusText = $"“{source.Name}”已重新排序";
            BringGroupIntoView(source);
        }
    }

    /// <summary>鼠标落在标签的右半边 → 插到它后面，左半边 → 插到前面。</summary>
    private void UpdateGroupDropMarker(DragEventArgs e)
    {
        var target = GroupFrom(e.OriginalSource);
        var source = _tabDragSource;

        foreach (var g in ViewModel.Groups)
            if (!ReferenceEquals(g, target)) g.ClearDragMarker();

        if (target is null || source is null || ReferenceEquals(target, source)) return;

        if (TabList.ItemContainerGenerator.ContainerFromItem(target) is not ListBoxItem container ||
            container.ActualWidth <= 0)
            return;

        _tabInsertAfter = e.GetPosition(container).X > container.ActualWidth / 2;
        if (_tabInsertAfter) target.IsDragTargetAfter = true;
        else target.IsDragTarget = true;
    }

    private void ClearGroupDragMarkers()
    {
        foreach (var g in ViewModel.Groups) g.ClearDragMarker();
    }

    private void TabList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedGroup is { } group) BeginGroupRename(group);
    }

    private void BeginGroupRename(ShortcutGroupViewModel group)
    {
        group.BeginRename();
        FocusRenameBox(container: TabList.ItemContainerGenerator.ContainerFromItem(group));
    }

    // ==================================================================
    //  磁贴交互：双击打开 / 选中
    // ==================================================================

    private ShortcutViewModel? ItemFrom(object? source)
    {
        if (source is not DependencyObject d) return null;
        var container = FindAncestor<ListBoxItem>(d);
        return container?.DataContext as ShortcutViewModel;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void TileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is not { } item) return;
        e.Handled = true;
        OpenItem(item);
    }

    private void OpenItem(ShortcutViewModel item)
    {
        if (item.IsRenaming) return;
        if (!item.Exists)
        {
            ViewModel.StatusText = $"路径不存在：{item.Path}";
            return;
        }
        Launcher.Open(item.Model, msg => ViewModel.StatusText = msg);
        ViewModel.StatusText = $"已打开 {item.Name}";
    }

    private void TileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SelectedItem is { } item)
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    // ==================================================================
    //  拖拽：内部排序 + 外部文件拖入
    // ==================================================================

    private void TileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = ItemFrom(e.OriginalSource);
    }

    private void TileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;
        if (IsDraggingRenameBox()) return;

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragCandidate;
        _dragCandidate = null;

        var data = new DataObject(InternalDragFormat, item);
        _dragSource = item;
        try
        {
            DragDrop.DoDragDrop(TileList, data, DragDropEffects.Move);
        }
        finally
        {
            _dragSource = null;
            ClearDragMarkers();
        }
    }

    private bool IsDraggingRenameBox()
        => Keyboard.FocusedElement is TextBox;

    private const string InternalDragFormat = "ShortcutManager.TileItem";

    private void TileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
    }

    private void TileList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormat) && _dragSource is not null)
        {
            e.Effects = DragDropEffects.Move;
            UpdateDropMarker(e);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            ClearDragMarkers();
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void TileList_DragLeave(object sender, DragEventArgs e) => ClearDragMarkers();

    private void TileList_Drop(object sender, DragEventArgs e)
    {
        ClearDragMarkers();
        e.Handled = true;

        // 1) 外部文件/文件夹拖入 → 导入当前分组
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var n = ViewModel.Import(files);
            ViewModel.StatusText = n > 0 ? $"已导入 {n} 项" : "没有可导入的新项目";
            return;
        }

        // 2) 内部拖拽 → 排序
        if (!e.Data.GetDataPresent(InternalDragFormat)) return;
        if (e.Data.GetData(InternalDragFormat) is not ShortcutViewModel source) return;

        var target = ItemFrom(e.OriginalSource);
        if (target is null)
        {
            ViewModel.MoveToEnd(source);
            return;
        }
        if (ReferenceEquals(target, source)) return;

        ViewModel.Reorder(source, target, _insertAfter);
    }

    private void UpdateDropMarker(DragEventArgs e)
    {
        var target = ItemFrom(e.OriginalSource);
        var source = _dragSource;

        foreach (var g in ViewModel.Groups)
            foreach (var i in g.Items)
                if (!ReferenceEquals(i, target)) i.ClearDragMarker();

        if (target is null || source is null || ReferenceEquals(target, source))
            return;

        var pos = e.GetPosition(GetContainer(target));
        _insertAfter = pos.X > GetContainer(target).ActualWidth / 2;
        if (_insertAfter) target.IsDragTargetAfter = true;
        else target.IsDragTarget = true;
    }

    private static ListBoxItem GetContainer(ShortcutViewModel item)
    {
        var container = item.Owner is null ? null : FindContainer(item);
        return container ?? new ListBoxItem();
    }

    private static ListBoxItem? FindContainer(ShortcutViewModel item)
    {
        if (Application.Current.MainWindow is not MainWindow mw) return null;
        return mw.TileList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
    }

    private void ClearDragMarkers()
    {
        foreach (var g in ViewModel.Groups)
            foreach (var i in g.Items)
                i.ClearDragMarker();
    }

    // 整个窗口也能接收拖入（落在空白处也算）
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var n = ViewModel.Import(files);
            ViewModel.StatusText = n > 0 ? $"已导入 {n} 项" : "没有可导入的新项目";
            e.Handled = true;
        }
    }

    // ==================================================================
    //  右键菜单
    // ==================================================================

    private void TileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTiles();
        var count = selected.Count;
        _menuTarget = ViewModel.SelectedItem ?? selected.FirstOrDefault();
        var has = _menuTarget is not null;
        var single = count == 1;          // 多选时只有“移除/复制路径”这类逐项操作有意义
        var any = count > 0;

        OpenMenu.IsEnabled = single;
        RevealMenu.IsEnabled = has;
        RenameMenu.IsEnabled = single && _menuTarget is { IsRenaming: false };
        EditPathMenu.IsEnabled = single;
        SendToMenu.IsEnabled = single;
        RemoveMenu.IsEnabled = any;
        OpenParentMenu.IsEnabled = has;
        CopyPathMenu.IsEnabled = any;

        RemoveMenu.Header = count > 1 ? $"移除 {count} 项" : "移除";

        SendToMenu.Items.Clear();
        if (_menuTarget is not null)
        {
            var others = ViewModel.Groups.Where(g => !ReferenceEquals(g, _menuTarget.Owner)).ToList();
            if (others.Count == 0)
            {
                SendToMenu.Items.Add(new MenuItem { Header = "（没有其他分组）", IsEnabled = false });
            }
            else
            {
                foreach (var group in others)
                {
                    var mi = new MenuItem { Header = group.Name, Tag = group };
                    mi.Click += SendToGroup_Click;
                    SendToMenu.Items.Add(mi);
                }
            }
        }
    }

    /// <summary>当前选中的磁贴（Ctrl/Shift 多选时可能不止一个）。</summary>
    private List<ShortcutViewModel> SelectedTiles()
        => TileList.SelectedItems.OfType<ShortcutViewModel>().ToList();

    /// <summary>
    /// 右键落在未选中的磁贴上时，先把选择收敛到它（和资源管理器一致）；
    /// 右键点在已选中的磁贴上则保留当前多选，这样“移除 N 项”才能批量生效。
    /// </summary>
    private void TileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is not { } item) return;   // 右键空白处：保留现有选择
        if (TileList.SelectedItems.Contains(item)) return;

        TileList.SelectedItems.Clear();
        TileList.SelectedItem = item;
    }

    /// <summary>右键菜单里的“移除”入口。</summary>
    private void SendToGroup_Click(object sender, RoutedEventArgs e)    {
        if (sender is not MenuItem { Tag: ShortcutGroupViewModel group }) return;
        if (_menuTarget is null) return;
        if (ViewModel.MoveItemToGroup(_menuTarget, group))
            ViewModel.StatusText = $"已移动到“{group.Name}”";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item) OpenItem(item);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item) Launcher.RevealInExplorer(item.Model);
    }

    private void OpenParent_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } item) return;
        var parent = item.IsDirectory
            ? Directory.GetParent(item.Path)?.FullName
            : System.IO.Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            ViewModel.StatusText = "找不到所在目录";
            return;
        }
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + parent + "\"", UseShellExecute = true }); }
        catch (Exception ex) { ViewModel.StatusText = ex.Message; }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        // 多选时把所有选中项的路径一起复制（每行一个）
        var paths = SelectedTiles().Select(i => i.Path).ToList();
        if (paths.Count == 0)
        {
            if (_menuTarget is { } only) paths.Add(only.Path);
        }
        if (paths.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
            ViewModel.StatusText = paths.Count == 1 ? "路径已复制" : $"已复制 {paths.Count} 条路径";
        }
        catch { /* 剪贴板偶发占用 */ }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item) BeginItemRename(item);
    }

    private void EditPath_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } item) return;

        var dlg = new PathEditDialog(item.Path) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var full = PathResolver.NormalizeUserInput(dlg.PathText);
        if (full is null)
        {
            ViewModel.StatusText = "路径无效，未修改";
            return;
        }

        if (!Directory.Exists(full) && !File.Exists(full))
        {
            var answer = MessageBox.Show(this,
                $"路径不存在：\n{full}\n\n仍要保存吗？",
                "编辑路径", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        // 同组内已有该路径的条目时拒绝，避免换路径后撞车
        var key = ShortcutItem.NormalizePath(full);
        if (item.Owner.Items.Any(i => !ReferenceEquals(i, item) && i.Model.PathKey == key))
        {
            ViewModel.StatusText = "同组已存在该路径的条目";
            return;
        }

        item.ChangePath(full);
        ViewModel.Save();
        ViewModel.StatusText = item.StoredPath == full
            ? $"路径已更新：{item.Name}"
            : $"路径已更新（相对路径存储）：{item.Name}";
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelectedItems();

    /// <summary>删除选中的磁贴；多选时一次删掉全部（只是从列表里移除，不动磁盘文件）。</summary>
    private void RemoveSelectedItems()
    {
        var items = SelectedTiles();
        if (items.Count == 0) return;

        var name = items[0].Name;
        var removed = ViewModel.RemoveItems(items);
        if (removed <= 0) return;

        ViewModel.StatusText = removed == 1
            ? $"已移除 {name}（磁盘文件未删除）"
            : $"已移除 {removed} 项（磁盘文件未删除）";
    }

    private void BeginItemRename(ShortcutViewModel item)
    {
        item.BeginRename();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            FocusRenameBox(container: TileList.ItemContainerGenerator.ContainerFromItem(item))));
    }

    // ==================================================================
    //  内联重命名输入框
    // ==================================================================

    private static void FocusRenameBox(DependencyObject? container)
    {
        if (container is null) return;
        var box = FindDescendant<TextBox>(container);
        if (box is null) return;
        box.Focus();
        box.SelectAll();
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>输入框真正显示出来后再抢焦点（可见性变化时浏览器里还没有布局）。</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }));
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var vm = box.DataContext;

        if (e.Key == Key.Enter)
        {
            CommitRename(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            CommitRename(vm);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitRename(box.DataContext);
    }

    private void CommitRename(object? vm)
    {
        switch (vm)
        {
            case ShortcutViewModel item when item.IsRenaming:
                item.CommitRename();
                ViewModel.Save();
                break;
            case ShortcutGroupViewModel group when group.IsRenaming:
                group.CommitRename();
                group.NotifyCountChanged();
                ViewModel.Save();
                break;
        }
    }

    private void CancelRename(object? vm)
    {
        switch (vm)
        {
            case ShortcutViewModel item when item.IsRenaming:
                item.CancelRename();
                break;
            case ShortcutGroupViewModel group when group.IsRenaming:
                group.IsRenaming = false;
                break;
        }
    }

    private bool TryCancelAnyRename()
    {
        var handled = false;
        if (ViewModel.SelectedItem is { IsRenaming: true } item) { item.CancelRename(); handled = true; }
        if (ViewModel.SelectedGroup is { IsRenaming: true } group) { group.IsRenaming = false; handled = true; }
        return handled;
    }

    // ==================================================================
    //  底部按钮
    // ==================================================================

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要添加的文件",
            Multiselect = true,
            Filter = "所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            var n = ViewModel.Import(dlg.FileNames);
            ViewModel.StatusText = n > 0 ? $"已导入 {n} 个文件" : "没有可导入的新文件";
        }
    }

    private void ImportFolders_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要添加的文件夹", Multiselect = true };
        if (dlg.ShowDialog(this) == true)
        {
            var n = ViewModel.Import(dlg.FolderNames);
            ViewModel.StatusText = n > 0 ? $"已导入 {n} 个文件夹" : "没有可导入的新文件夹";
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshAll();
        ViewModel.StatusText = "已刷新";
    }
}
