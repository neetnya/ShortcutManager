using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    public static readonly RoutedCommand FindCommand = new();
    public static readonly RoutedCommand UndoCommand = new();

    private Point _dragStart;
    private ShortcutViewModel? _dragCandidate;
    private ShortcutViewModel? _dragSource;
    private ShortcutViewModel? _lastTileDropTarget;
    private ShortcutViewModel? _menuTarget;
    private bool _insertAfter;

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    public MainViewModel ViewModel { get; }
    internal AppSettings Settings => _settings;

    /// <summary>真正需要退出进程时触发（关闭按钮=退出、托盘菜单“退出”）。</summary>
    public event Action? ExitRequested;

    public MainWindow(MainViewModel viewModel, AppSettings settings, SettingsStore settingsStore)
    {
        ViewModel = viewModel;
        _settings = settings;
        _settingsStore = settingsStore;
        DataContext = viewModel;
        InitializeComponent();

        CommandBindings.Add(new CommandBinding(MinimizeCommand, (_, _) => WindowState = WindowState.Minimized));
        CommandBindings.Add(new CommandBinding(NewGroupCommand, (_, _) => CreateGroup()));
        CommandBindings.Add(new CommandBinding(DeleteCommand, (_, _) =>
        {
            if (Keyboard.FocusedElement is TextBox) return;
            RemoveSelectedItems();
        }));
        CommandBindings.Add(new CommandBinding(RenameCommand, (_, _) =>
        {
            if (Keyboard.FocusedElement is TextBox) return;
            if (ViewModel.SelectedItem is { } item) BeginItemRename(item);
        }));
        CommandBindings.Add(new CommandBinding(QuitCommand, (_, _) => ExitApplication()));
        CommandBindings.Add(new CommandBinding(FindCommand, (_, _) =>
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }));
        CommandBindings.Add(new CommandBinding(UndoCommand, (_, _) =>
        {
            if (Keyboard.FocusedElement is TextBox) return;
            ViewModel.UndoLastRemove();
        }));

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        LocationChanged += (_, _) => ScheduleGeometrySave();
        SizeChanged += (_, _) => ScheduleGeometrySave();
        ApplySavedGeometry();
        AllowDrop = true;
        Drop += Window_Drop;
        DragOver += Window_DragOver;

        // 标签栏翻页箭头：监听滚动、尺寸与分组集合变化（替代高频全局 LayoutUpdated）
        TabScroll.ScrollChanged += (_, _) => UpdateTabArrows();
        TabScroll.SizeChanged += (_, _) => UpdateTabArrows();
        TabList.SizeChanged += (_, _) => UpdateTabArrows();
        TabList.SelectionChanged += TabList_SelectionChanged;
        ViewModel.Groups.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateTabArrows));

        ApplyPinned(_settings.AlwaysOnTop, save: false);

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
        EnsureOnScreen();
        UpdateTabArrows();

        // 在 UI 线程初始化图标 DPI，然后后台探测状态并按当前分组优先预热图标
        ShellIcons.InitializeDpi(this);
        _ = ViewModel.ProbeAllStatusAsync();
        ViewModel.WarmAllIcons(prioritizeSelected: true);

        AutomationRunner.TryRun(this);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ShellIcons.InitializeDpi(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyRoundedCorners(this);
        WindowEffects.DisableMaximize(this);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            CleanupOnExit();
            return;
        }

        if (_forceExit)
        {
            ExitApplication();
            return;
        }

        if (_settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
        }
        else
        {
            ExitApplication();
        }
    }

    /// <summary>统一退出清理（幂等）：保存窗口位置、配置并释放托盘，避免重复写盘或漏释句柄。</summary>
    internal void CleanupOnExit()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _geometrySaveTimer?.Stop();
        _frontTimer?.Stop();
        SaveGeometryNow();
        ViewModel.Save();
        _tray?.Dispose();
        _tray = null;
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        CleanupOnExit();
        ExitRequested?.Invoke();
    }

    internal void ExitApplicationForTest()
    {
        _forceExit = true;
        ExitApplication();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (TryCancelAnyRename()) { e.Handled = true; return; }
            if (!string.IsNullOrWhiteSpace(ViewModel.FilterText))
            {
                ViewModel.FilterText = string.Empty;
                e.Handled = true;
                return;
            }
            WindowState = WindowState.Minimized;
            e.Handled = true;
        }
    }

    // ==================================================================
    //  托盘管理
    // ==================================================================

    private TrayIcon? _tray;
    private bool _inTray;
    private bool _exiting;
    private bool _forceExit;
    private bool _cleanedUp;

    internal bool IsTrayIconVisible => _tray is { IsVisible: true };

    internal void SimulateTrayClick(System.Windows.Forms.MouseButtons button) => Tray.SimulateClick(button);

    private TrayIcon Tray
    {
        get
        {
            if (_tray is null)
            {
                _tray = new TrayIcon();
                _tray.Activated += RestoreFromTray;
                _tray.ExitRequested += ExitApplication;
                _tray.SettingsRequested += OpenSettings;
            }
            return _tray;
        }
    }

    private void HideToTray()
    {
        if (_inTray || _exiting) return;

        SaveGeometryNow();
        _inTray = true;
        Tray.Show();
        ShowInTaskbar = false;
        Hide();
        ViewModel.StatusText = "";
        MaybeShowTrayHint();
    }

    public void HideToTrayOnStartup()
    {
        _inTray = true;
        ShowInTaskbar = false;
        Show();
        Tray.Show();
        Hide();
    }

    public void RestoreFromTray()
    {
        if (_exiting) return;

        _inTray = false;
        EnsureOnScreen();
        ShowInTaskbar = true;
        Show();
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        App.ForceForeground(this);
    }

    private void OpenSettings() => ((App)Application.Current).OpenSettings();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ==================================================================
    //  窗口位置 / 第一次进托盘提示
    // ==================================================================

    private DispatcherTimer? _geometrySaveTimer;

    private void ApplySavedGeometry()
    {
        if (_settings.WindowWidth >= MinWidth) Width = _settings.WindowWidth;
        if (_settings.WindowHeight >= MinHeight) Height = _settings.WindowHeight;
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    /// <summary>显示器被拔掉后，窗口可能落在虚拟桌面外，拉回可见区域。</summary>
    private void EnsureOnScreen()
    {
        var vsL = SystemParameters.VirtualScreenLeft;
        var vsT = SystemParameters.VirtualScreenTop;
        var vsR = vsL + SystemParameters.VirtualScreenWidth;
        var vsB = vsT + SystemParameters.VirtualScreenHeight;
        if (Left + Width < vsL + 80 || Left > vsR - 80 || Top + 32 < vsT || Top > vsB - 40)
        {
            Left = vsL + Math.Max(0, (SystemParameters.VirtualScreenWidth - Width) / 2);
            Top = vsT + Math.Max(0, (SystemParameters.VirtualScreenHeight - Height) / 2);
        }
    }

    private void ScheduleGeometrySave()
    {
        if (App.IsAutomationRun || _inTray || _exiting) return;
        if (WindowState != WindowState.Normal) return;
        if (_geometrySaveTimer is null)
        {
            _geometrySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _geometrySaveTimer.Tick += OnGeometrySaveTick;
        }
        _geometrySaveTimer.Stop();
        _geometrySaveTimer.Start();
    }

    private void OnGeometrySaveTick(object? sender, EventArgs e)
    {
        _geometrySaveTimer?.Stop();
        SaveGeometryNow();
    }

    private void SaveGeometryNow()
    {
        if (App.IsAutomationRun || WindowState != WindowState.Normal) return;
        if (Width < MinWidth || Height < MinHeight) return;

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        try { _settingsStore.Save(_settings); } catch { /* ignore */ }
    }

    private void MaybeShowTrayHint()
    {
        if (App.IsAutomationRun || _settings.HasShownTrayHint) return;
        var hotkey = GlobalHotkeyManager.Format(_settings.HotkeyModifiers, _settings.HotkeyKey);
        Tray.ShowInfoBalloon("快捷方式管理器", $"已在托盘运行。单击图标或按 {hotkey} 可唤起。");
        _settings.HasShownTrayHint = true;
        try { _settingsStore.Save(_settings); } catch { /* ignore */ }
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
                BringGroupIntoView(g);
                BeginGroupRename(g);
                ViewModel.StatusText = "已新建分组";
            }
        }));
    }

    private void CleanupMissing_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGroup is not { } group) return;
        var missing = group.Items.Count(i => i.IsMissing);
        if (missing <= 0)
        {
            ViewModel.StatusText = "没有失效项";
            return;
        }

        var answer = MessageBox.Show(this,
            $"分组“{group.Name}”中有 {missing} 个路径已失效，要从列表里移除吗？\n\n不会删除磁盘上的文件。",
            "清理失效项", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var n = ViewModel.RemoveMissingItems(group);
        ViewModel.StatusText = n > 0 ? $"已清理 {n} 个失效项" : "没有失效项";
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

    private const double TabScrollStep = 120;
    private const double TabScrollEpsilon = 1.5;

    internal void UpdateTabArrows()
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

    internal void ScrollTabs(double delta)
    {
        var target = Math.Clamp(TabScroll.HorizontalOffset + delta, 0, TabScroll.ScrollableWidth);
        TabScroll.ScrollToHorizontalOffset(target);
        UpdateTabArrows();
    }

    private void BringGroupIntoView(ShortcutGroupViewModel group)
    {
        if (TabList.ItemContainerGenerator.ContainerFromItem(group) is not ListBoxItem container) return;
        if (container.ActualWidth <= 0) return;

        double left;
        try { left = container.TransformToAncestor(TabList).Transform(new Point(0, 0)).X; }
        catch { return; }

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
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => BringGroupIntoView(group)));
    }

    // ---- 分组标签拖拽排序 ----

    private const string GroupDragFormat = "ShortcutManager.GroupTab";

    private Point _tabDragStart;
    private ShortcutGroupViewModel? _tabDragCandidate;
    private ShortcutGroupViewModel? _tabDragSource;
    private ShortcutGroupViewModel? _lastGroupDropTarget;
    private bool _tabInsertAfter;

    private ShortcutGroupViewModel? GroupFrom(object? source)
    {
        if (source is not DependencyObject d) return null;
        return FindAncestor<ListBoxItem>(d)?.DataContext as ShortcutGroupViewModel;
    }

    private void TabList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GroupFrom(e.OriginalSource) is { } group)
            ViewModel.SelectedGroup = group;
    }

    private void TabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(this);
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
        else if (e.Data.GetDataPresent(InternalDragFormat) && _dragSource is not null)
        {
            e.Effects = DragDropEffects.Move;
            UpdateGroupDropHover(e);
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            UpdateGroupDropHover(e);
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void TabList_DragLeave(object sender, DragEventArgs e) => ClearGroupDragMarkers();

    private void TabList_Drop(object sender, DragEventArgs e)
    {
        ClearGroupDragMarkers();
        e.Handled = true;

        var target = GroupFrom(e.OriginalSource);

        if (e.Data.GetDataPresent(InternalDragFormat) && e.Data.GetData(InternalDragFormat) is ShortcutViewModel tile)
        {
            if (target is null) return;
            var batch = SelectedTiles();
            if (!batch.Contains(tile)) batch = new List<ShortcutViewModel> { tile };
            ViewModel.MoveItemsToGroup(batch, target);
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var dest = target ?? ViewModel.SelectedGroup;
            var n = ViewModel.Import(files, dest);
            if (dest is not null && n > 0) ViewModel.SelectedGroup = dest;
            ViewModel.StatusText = n > 0 ? $"已导入 {n} 项到“{dest?.Name}”" : "没有可导入的新项目";
            return;
        }

        if (!e.Data.GetDataPresent(GroupDragFormat)) return;
        if (e.Data.GetData(GroupDragFormat) is not ShortcutGroupViewModel source) return;

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

        if (_lastGroupDropTarget is not null && !ReferenceEquals(_lastGroupDropTarget, target))
            _lastGroupDropTarget.ClearDragMarker();

        if (target is null || source is null || ReferenceEquals(target, source))
        {
            _lastGroupDropTarget?.ClearDragMarker();
            _lastGroupDropTarget = null;
            return;
        }

        if (TabList.ItemContainerGenerator.ContainerFromItem(target) is not ListBoxItem container ||
            container.ActualWidth <= 0)
            return;

        _tabInsertAfter = e.GetPosition(container).X > container.ActualWidth / 2;
        target.IsDragTarget = !_tabInsertAfter;
        target.IsDragTargetAfter = _tabInsertAfter;
        _lastGroupDropTarget = target;
    }

    /// <summary>磁贴或外部文件拖到某颗标签上：整颗高亮，表示会进这一组。</summary>
    private void UpdateGroupDropHover(DragEventArgs e)
    {
        var target = GroupFrom(e.OriginalSource);
        if (ReferenceEquals(_lastGroupDropTarget, target)) return;

        _lastGroupDropTarget?.ClearDragMarker();
        _lastGroupDropTarget = target;
        if (target is not null) target.IsDropHover = true;
    }

    private void ClearGroupDragMarkers()
    {
        if (_lastGroupDropTarget is not null)
        {
            _lastGroupDropTarget.ClearDragMarker();
            _lastGroupDropTarget = null;
        }
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

        var model = item.Model;
        var name = item.Name;

        ViewModel.StatusText = $"正在打开 {name}…";
        KeepFrontBriefly();
        _ = ReportOpenResultAsync(model, name);
    }

    private async Task ReportOpenResultAsync(ShortcutItem model, string name)
    {
        var error = await Launcher.OpenAsync(model);
        ViewModel.StatusText = error ?? $"已打开 {name}";
    }

    // ==================================================================
    //  打开后的窗口行为：短暂置顶 / 钉住置顶
    // ==================================================================

    internal const int KeepFrontMilliseconds = 4000;

    private DispatcherTimer? _frontTimer;

    internal void KeepFrontBriefly()
    {
        if (!_settings.KeepFrontAfterOpen) return;
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        if (_frontTimer is null)
        {
            _frontTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(KeepFrontMilliseconds) };
            _frontTimer.Tick += (_, _) =>
            {
                _frontTimer?.Stop();
                Topmost = _settings.AlwaysOnTop;
            };
        }

        _frontTimer.Stop();
        Topmost = true;
        _frontTimer.Start();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
        => ApplyPinned(PinToggle.IsChecked == true, save: !App.IsAutomationRun);

    internal void ApplyPinned(bool pinned, bool save)
    {
        _settings.AlwaysOnTop = pinned;
        if (PinToggle.IsChecked != pinned) PinToggle.IsChecked = pinned;

        if (pinned)
        {
            _frontTimer?.Stop();
            Topmost = true;
        }
        else if (_frontTimer is not { IsEnabled: true })
        {
            Topmost = false;
        }

        PinToggle.ToolTip = pinned
            ? "窗口已置顶：新打开的程序不会再盖住它（再点一次取消）"
            : "窗口置顶：钉住后新打开的程序不会再盖住它";

        if (!save) return;
        try { _settingsStore.Save(_settings); } catch { /* ignore */ }
    }

    internal void ApplyWindowSettings() => ApplyPinned(_settings.AlwaysOnTop, save: false);

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
        if (Keyboard.FocusedElement is TextBox) return;

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

    private const string InternalDragFormat = "ShortcutManager.TileItem";

    private void TileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragCandidate = null;

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

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var n = ViewModel.Import(files);
            ViewModel.StatusText = n > 0 ? $"已导入 {n} 项" : "没有可导入的新项目";
            return;
        }

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

        if (_lastTileDropTarget is not null && !ReferenceEquals(_lastTileDropTarget, target))
            _lastTileDropTarget.ClearDragMarker();

        if (target is null || source is null || ReferenceEquals(target, source))
        {
            _lastTileDropTarget?.ClearDragMarker();
            _lastTileDropTarget = null;
            return;
        }

        if (TileList.ItemContainerGenerator.ContainerFromItem(target) is not ListBoxItem container ||
            container.ActualWidth <= 0)
            return;

        _insertAfter = e.GetPosition(container).X > container.ActualWidth / 2;
        target.IsDragTarget = !_insertAfter;
        target.IsDragTargetAfter = _insertAfter;
        _lastTileDropTarget = target;
    }

    private void ClearDragMarkers()
    {
        if (_lastTileDropTarget is not null)
        {
            _lastTileDropTarget.ClearDragMarker();
            _lastTileDropTarget = null;
        }
    }

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
        var single = count == 1;
        var any = count > 0;

        OpenMenu.IsEnabled = single;
        RevealMenu.IsEnabled = has;
        RenameMenu.IsEnabled = single && _menuTarget is { IsRenaming: false };
        EditPathMenu.IsEnabled = single;
        SendToMenu.IsEnabled = any;
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

    internal List<ShortcutViewModel> SelectedTiles()
        => TileList.SelectedItems.OfType<ShortcutViewModel>().ToList();

    private void TileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is not { } item) return;
        if (TileList.SelectedItems.Contains(item)) return;

        TileList.SelectedItems.Clear();
        TileList.SelectedItem = item;
    }

    private void SendToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ShortcutGroupViewModel group }) return;
        var batch = SelectedTiles();
        if (batch.Count == 0 && _menuTarget is not null) batch.Add(_menuTarget);
        if (batch.Count == 0) return;
        ViewModel.MoveItemsToGroup(batch, group);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item) OpenItem(item);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item) _ = Launcher.RevealInExplorerAsync(item.Model);
    }

    private async void OpenParent_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } item) return;
        var error = await Launcher.OpenParentDirectoryAsync(item.Model);
        if (!string.IsNullOrEmpty(error))
            ViewModel.StatusText = error;
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedTiles().Select(i => i.Path).ToList();
        if (paths.Count == 0 && _menuTarget is { } only)
            paths.Add(only.Path);
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

    internal void RemoveSelectedItems()
    {
        var items = SelectedTiles();
        if (items.Count == 0) return;

        var name = items[0].Name;
        var removed = ViewModel.RemoveItems(items);
        if (removed <= 0) return;

        ViewModel.StatusText = removed == 1
            ? $"已移除 {name}（Ctrl+Z 撤销）"
            : $"已移除 {removed} 项（Ctrl+Z 撤销）";
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

        if (e.Key is Key.Enter or Key.Tab)
        {
            CommitRename(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename(vm);
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

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StatusText = "正在刷新…";
        await ViewModel.RefreshAllAsync();
        ViewModel.StatusText = "已刷新";
    }
}
