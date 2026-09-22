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
        CommandBindings.Add(new CommandBinding(DeleteCommand, (_, _) => RemoveSelectedItem()));
        CommandBinding renameBinding = new(RenameCommand, (_, _) =>
        {
            if (ViewModel.SelectedItem is { } item) BeginItemRename(item);
        });
        CommandBindings.Add(renameBinding);
        CommandBindings.Add(new CommandBinding(QuitCommand, (_, _) => Close()));

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        AllowDrop = true;
        Drop += Window_Drop;
        DragOver += Window_DragOver;

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
                $"Groups={ViewModel.Groups.Count} Items={ViewModel.SelectedItems?.Count}",
            };
            TryWriteLog("diag.log", lines);
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
            TryWriteLog("shot.log", new[]
            {
                $"saved={path}",
                $"size(DIP)={w}x{h}",
                $"transform={src?.CompositionTarget?.TransformToDevice}",
                $"groups={ViewModel.Groups.Count} items={ViewModel.SelectedItems?.Count}",
            });
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
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭就是关闭：保存配置后让窗口正常关闭，进程随之退出。
        // （ShutdownMode = OnMainWindowClose）
        ViewModel.Save();
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

    private void MoveGroupLeft_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGroup is { } g) ViewModel.MoveGroup(g, -1);
    }

    private void MoveGroupRight_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGroup is { } g) ViewModel.MoveGroup(g, +1);
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
        _menuTarget = ViewModel.SelectedItem;
        var has = _menuTarget is not null;

        if (sender is not ContextMenu menu) return;
        foreach (var obj in menu.Items)
        {
            if (obj is MenuItem mi && mi.Header is string header &&
                (header == "打开" || header == "在资源管理器中显示" || header == "重命名" || header == "编辑路径" ||
                 header == "发送到其他分组" || header == "移除" || header == "打开所在目录" || header == "复制路径"))
            {
                mi.IsEnabled = has;
            }
        }

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
        if (_menuTarget is not { } item) return;
        try
        {
            Clipboard.SetText(item.Path);
            ViewModel.StatusText = "路径已复制";
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

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelectedItem();

    private void RemoveSelectedItem()
    {
        if (ViewModel.SelectedItem is not { } item) return;
        ViewModel.RemoveItem(item);
        ViewModel.StatusText = $"已移除 {item.Name}（磁盘文件未删除）";
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
