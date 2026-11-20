using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Data;
using ShortcutManager.Models;
using ShortcutManager.Services;

namespace ShortcutManager.ViewModels;

/// <summary>整个应用的状态。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private ShortcutGroupViewModel? _selectedGroup;
    private ShortcutViewModel? _selectedItem;
    private string _statusText = string.Empty;
    private string _filterText = string.Empty;
    private RemovedSlice? _undoRemove;
    private bool _warnedFallback;

    /// <summary>
    /// 初始化视图模型。
    /// 当 <paramref name="deferDiskProbe"/> 为 true（UI 启动路径）时，不在 UI 线程同步查磁盘，
    /// 由调用方在窗口显示后通过 <see cref="ProbeAllStatusAsync"/> 后台探测。
    /// </summary>
    public MainViewModel(ConfigStore store, List<ShortcutGroup> groups, bool deferDiskProbe = false)
    {
        _store = store;
        foreach (var g in groups)
        {
            if (!deferDiskProbe)
            {
                foreach (var item in g.Items)
                    item.IsDirectory = Directory.Exists(item.FullPath);
            }

            Groups.Add(new ShortcutGroupViewModel(g, initialItemExists: deferDiskProbe ? true : null));
        }

        _selectedGroup = Groups.FirstOrDefault();
        if (_selectedGroup is not null)
            _selectedGroup.Items.CollectionChanged += OnSelectedItemsChanged;
    }

    public ObservableCollection<ShortcutGroupViewModel> Groups { get; } = new();

    public ShortcutGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (ReferenceEquals(_selectedGroup, value)) return;
            if (_selectedGroup is not null)
                _selectedGroup.Items.CollectionChanged -= OnSelectedItemsChanged;
            if (Set(ref _selectedGroup, value))
            {
                if (value is not null)
                    value.Items.CollectionChanged += OnSelectedItemsChanged;
                foreach (var g in Groups) g.NotifyCountChanged();
                Raise(nameof(SelectedItems));
                ApplyFilter();
                RaiseEmpty();
            }
        }
    }

    public ObservableCollection<ShortcutViewModel>? SelectedItems => _selectedGroup?.Items;

    /// <summary>当前选中的格子（键盘 Delete / 右键菜单用）。</summary>
    public ShortcutViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            if (_selectedItem is not null) _selectedItem.IsSelected = false;
            _selectedItem = value;
            if (_selectedItem is not null) _selectedItem.IsSelected = true;
            Raise();
            Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedItem is not null;

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!Set(ref _filterText, value)) return;
            ApplyFilter();
            Raise(nameof(HasActiveFilter));
            RaiseEmpty();
        }
    }

    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_filterText);

    public bool ShowEmptyState
    {
        get
        {
            if (SelectedItems is null || SelectedItems.Count == 0) return true;
            if (!HasActiveFilter) return false;
            var view = CollectionViewSource.GetDefaultView(SelectedItems);
            return view is null || !view.Cast<object>().Any();
        }
    }

    public string EmptyTitle => HasActiveFilter ? "没有匹配的项目" : "把文件或文件夹拖到这里";

    public string EmptyHint => HasActiveFilter ? "试试别的关键字，或按 Esc 清空" : "也可以点下方“导入文件 / 导入文件夹”";

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseEmpty();

    private void ApplyFilter()
    {
        if (SelectedItems is null) return;
        var view = CollectionViewSource.GetDefaultView(SelectedItems);
        if (view is null) return;
        var q = _filterText.Trim();
        if (q.Length == 0) view.Filter = null;
        else view.Filter = obj => obj is ShortcutViewModel item && ItemMatches(item, q);
        view.Refresh();
    }

    private static bool ItemMatches(ShortcutViewModel item, string q)
        => item.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
           || item.Path.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void RaiseEmpty()
    {
        Raise(nameof(ShowEmptyState));
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptyHint));
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public ShortcutGroupViewModel? FindGroup(string id)
        => Groups.FirstOrDefault(g => string.Equals(g.Model.Id, id, StringComparison.OrdinalIgnoreCase));

    public void AddGroup(ShortcutGroup? group = null)
    {
        group ??= new ShortcutGroup { Name = NextGroupName() };
        var vm = new ShortcutGroupViewModel(group);
        Groups.Add(vm);
        SelectedGroup = vm;
        Save();
    }

    private string NextGroupName()
    {
        var i = Groups.Count + 1;
        while (Groups.Any(g => g.Name == $"分组 {i}")) i++;
        return $"分组 {i}";
    }

    /// <summary>移除分组；至少保留一个。</summary>
    public bool RemoveGroup(ShortcutGroupViewModel group)
    {
        if (Groups.Count <= 1)
        {
            StatusText = "至少保留一个分组";
            return false;
        }

        var index = Groups.IndexOf(group);
        Groups.Remove(group);
        SelectedGroup = Groups[Math.Clamp(index, 0, Groups.Count - 1)];
        Save();
        return true;
    }

    /// <summary>分组拖拽排序：把 source 移到 target 的前/后。返回是否真的发生了位移（没位移就不写盘）。</summary>
    public bool MoveGroupTo(ShortcutGroupViewModel source, ShortcutGroupViewModel target, bool insertAfter)
    {
        if (ReferenceEquals(source, target)) return false;

        var from = Groups.IndexOf(source);
        if (from < 0) return false;

        Groups.RemoveAt(from);
        var to = Groups.IndexOf(target);
        if (to < 0) to = Groups.Count - 1;
        if (insertAfter) to++;

        to = Math.Clamp(to, 0, Groups.Count);
        Groups.Insert(to, source);

        if (to == from) return false;

        Save();
        return true;
    }

    /// <summary>分组拖到标签栏空白处 → 移到最后。</summary>
    public bool MoveGroupToEnd(ShortcutGroupViewModel source)
    {
        var from = Groups.IndexOf(source);
        if (from < 0 || from == Groups.Count - 1) return false;
        Groups.RemoveAt(from);
        Groups.Add(source);
        Save();
        return true;
    }

    /// <summary>把路径导入到指定分组（默认当前分组）。</summary>
    public int Import(IEnumerable<string> paths, ShortcutGroupViewModel? target = null)
    {
        target ??= SelectedGroup;
        if (target is null) return 0;

        var created = ImportService.CreateItems(paths, target.Model);
        if (created.Count == 0) return 0;

        foreach (var item in created)
        {
            target.Model.Items.Add(item);
            target.Items.Add(new ShortcutViewModel(item, target, initialExists: true));
        }
        target.NotifyCountChanged();
        Save();
        ImportService.WarmIcons(created);
        return created.Count;
    }

    public void RemoveItem(ShortcutViewModel item) => RemoveItems(new[] { item });

    /// <summary>
    /// 批量移除条目（多选删除用）：只从列表里删掉，磁盘文件不动。
    /// 返回真正移除的数量；一次写盘，不是每项写一次。
    /// </summary>
    public int RemoveItems(IEnumerable<ShortcutViewModel> items)
    {
        var list = items.Distinct().ToList();
        var snapshot = list
            .Select(i => (item: i, index: i.Owner.Items.IndexOf(i)))
            .Where(x => x.index >= 0)
            .OrderBy(x => x.index)
            .ToList();

        var removed = 0;
        foreach (var item in list)
        {
            var group = item.Owner;
            if (!group.Items.Remove(item)) continue;

            group.Model.Items.Remove(item.Model);
            group.NotifyCountChanged();
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            removed++;
        }

        if (removed > 0)
        {
            if (snapshot.Count > 0)
            {
                _undoRemove = new RemovedSlice
                {
                    Entries = snapshot
                        .Select(x => new RemovedEntry(x.item.Owner, x.index, x.item.Model))
                        .ToList(),
                };
            }
            Save();
            RaiseEmpty();
        }
        return removed;
    }

    public bool CanUndoRemove
        => _undoRemove is not null
           && DateTime.UtcNow - _undoRemove.At < TimeSpan.FromSeconds(15)
           && _undoRemove.Entries.Any(e => Groups.Contains(e.Group));

    /// <summary>撤销上一次移除，按原分组与原索引精确插回。</summary>
    public bool UndoLastRemove()
    {
        if (!CanUndoRemove || _undoRemove is null) return false;
        var slice = _undoRemove;
        _undoRemove = null;

        var touchedGroups = new HashSet<ShortcutGroupViewModel>();
        var restored = 0;

        // 按原索引升序插回，确保不连续多选也能恢复各自原始位置
        foreach (var entry in slice.Entries.OrderBy(e => e.OriginalIndex))
        {
            if (!Groups.Contains(entry.Group)) continue;
            if (entry.Group.Model.Items.Any(x => x.PathKey == entry.Model.PathKey)) continue;

            var at = Math.Clamp(entry.OriginalIndex, 0, entry.Group.Items.Count);
            entry.Group.Model.Items.Insert(at, entry.Model);
            entry.Group.Items.Insert(at, new ShortcutViewModel(entry.Model, entry.Group));
            touchedGroups.Add(entry.Group);
            restored++;
        }

        if (restored == 0) return false;
        foreach (var g in touchedGroups) g.NotifyCountChanged();
        Save();
        RaiseEmpty();
        StatusText = restored == 1 ? "已撤销移除" : $"已撤销移除 {restored} 项";
        return true;
    }

    /// <summary>移除本组中路径已不存在的条目（不动磁盘）。</summary>
    public int RemoveMissingItems(ShortcutGroupViewModel? group = null)
    {
        group ??= SelectedGroup;
        if (group is null) return 0;
        var missing = group.Items.Where(i => i.IsMissing).ToList();
        return RemoveItems(missing);
    }

    /// <summary>把条目移动到其他分组（追加到末尾）。目标已有同一路径时不改任何一组。</summary>
    public bool MoveItemToGroup(ShortcutViewModel item, ShortcutGroupViewModel target, bool save = true)
    {
        if (ReferenceEquals(item.Owner, target)) return false;

        var model = item.Model;
        if (target.Model.Items.Any(i => i.PathKey == model.PathKey))
        {
            StatusText = $"“{target.Name}”中已存在该项";
            return false;
        }

        var source = item.Owner;
        source.Model.Items.Remove(model);
        source.Items.Remove(item);
        source.NotifyCountChanged();

        target.Model.Items.Add(model);
        target.Items.Add(new ShortcutViewModel(model, target, initialExists: item.Exists));
        target.NotifyCountChanged();

        if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
        if (save) Save();
        return true;
    }

    /// <summary>批量发送到其他分组；目标已有的跳过，其余一次写盘。</summary>
    public int MoveItemsToGroup(IEnumerable<ShortcutViewModel> items, ShortcutGroupViewModel target)
    {
        var moved = 0;
        var skipped = 0;
        foreach (var item in items.Distinct().ToList())
        {
            if (ReferenceEquals(item.Owner, target)) continue;
            if (target.Model.Items.Any(i => i.PathKey == item.Model.PathKey))
            {
                skipped++;
                continue;
            }
            if (MoveItemToGroup(item, target, save: false)) moved++;
        }

        if (moved > 0) Save();
        if (moved > 0 && skipped > 0)
            StatusText = $"已移动 {moved} 项到“{target.Name}”，{skipped} 项已存在";
        else if (moved > 0)
            StatusText = moved == 1 ? $"已移动到“{target.Name}”" : $"已移动 {moved} 项到“{target.Name}”";
        else if (skipped > 0)
            StatusText = $"“{target.Name}”中已存在所选项目";
        return moved;
    }

    /// <summary>组内排序：把 source 移动到 target 的前/后。</summary>
    public void Reorder(ShortcutViewModel source, ShortcutViewModel target, bool insertAfter)
    {
        var group = source.Owner;
        if (!ReferenceEquals(group, target.Owner)) return;

        var items = group.Items;
        var models = group.Model.Items;

        var from = items.IndexOf(source);
        if (from < 0) return;

        items.RemoveAt(from);
        models.RemoveAt(from);

        var to = items.IndexOf(target);
        if (to < 0) to = items.Count - 1;
        if (insertAfter) to++;

        to = Math.Clamp(to, 0, items.Count);
        if (to == from)
        {
            items.Insert(from, source);
            models.Insert(from, source.Model);
            return;
        }

        items.Insert(to, source);
        models.Insert(to, source.Model);

        Save();
    }

    public void MoveToEnd(ShortcutViewModel source)
    {
        var group = source.Owner;
        var items = group.Items;
        var models = group.Model.Items;
        var from = items.IndexOf(source);
        if (from < 0 || from == items.Count - 1) return;

        items.RemoveAt(from);
        models.RemoveAt(from);
        items.Add(source);
        models.Add(source.Model);
        Save();
    }

    /// <summary>同步刷新所有条目的图标与存在状态（兼容非 UI 调用）。</summary>
    public void RefreshAll()
    {
        ShellIcons.ClearCache();
        foreach (var g in Groups)
        {
            g.NotifyCountChanged();
            foreach (var item in g.Items)
            {
                var isDir = Directory.Exists(item.Model.FullPath);
                item.Model.IsDirectory = isDir;
                var exists = isDir || File.Exists(item.Model.FullPath);
                item.Refresh(probedExists: exists);
            }
        }
        Save();
    }

    /// <summary>后台异步探测所有条目的存在状态（不阻塞 UI 线程，网络盘/慢盘友好）。</summary>
    public Task ProbeAllStatusAsync(bool reloadIcons = false)
    {
        if (reloadIcons) ShellIcons.ClearCache();

        // 在 UI 线程做好快照，避免后台线程遍历 ObservableCollection 抛并发修改异常
        var snapshot = Groups
            .SelectMany(g => g.Items)
            .Select(vm => (vm, path: vm.Path))
            .ToList();

        return Task.Run(() =>
        {
            var results = new List<(ShortcutViewModel vm, bool isDir, bool exists)>(snapshot.Count);
            foreach (var (vm, path) in snapshot)
            {
                var isDir = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
                var exists = isDir || (!string.IsNullOrWhiteSpace(path) && File.Exists(path));
                results.Add((vm, isDir, exists));
            }

            void Apply()
            {
                foreach (var (vm, isDir, exists) in results)
                    vm.ApplyProbedState(isDir, exists, reloadIcon: reloadIcons);
            }

            var disp = Application.Current?.Dispatcher;
            if (disp is not null) disp.BeginInvoke(Apply);
            else Apply();
        });
    }

    /// <summary>异步刷新全部图标与路径存在状态。</summary>
    public async Task RefreshAllAsync()
    {
        foreach (var g in Groups) g.NotifyCountChanged();
        await ProbeAllStatusAsync(reloadIcons: true);
        WarmAllIcons(prioritizeSelected: true);
    }

    /// <summary>在 UI 线程快照并提交后台图标预热队列（当前分组优先）。</summary>
    public void WarmAllIcons(bool prioritizeSelected = true)
    {
        var items = new List<ShortcutItem>();
        if (prioritizeSelected && SelectedGroup is not null)
        {
            items.AddRange(SelectedGroup.Items.Select(i => i.Model));
        }

        foreach (var g in Groups)
        {
            if (prioritizeSelected && ReferenceEquals(g, SelectedGroup)) continue;
            items.AddRange(g.Items.Select(i => i.Model));
        }

        ImportService.WarmIcons(items);
    }

    public void Save()
    {
        // 自动化验证（--uitest）会在界面里真的增删分组/条目，置位后一律不落盘
        if (SuppressSave) return;

        LastSaveError = string.Empty;
        try
        {
            _store.Save(Groups.Select(g => g.Model));

            if (_store.FellBackToAppData && !_warnedFallback)
            {
                _warnedFallback = true;
                StatusText = $"程序目录不可写，配置已改存到 {_store.FilePath}";
            }
        }
        catch (Exception ex)
        {
            LastSaveError = ex.ToString();
            StatusText = _store.IsPortableLocation
                ? "无法保存：程序目录和用户目录都不可写"
                : "无法保存：用户配置目录不可写";
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "ShortcutManager-save-error.log"),
                    $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>调试用：置为 true 后 <see cref="Save"/> 直接返回（--uitest 用，避免测试数据写进真实配置）。</summary>
    public bool SuppressSave { get; set; }

    /// <summary>调试用：最近一次保存的错误（无错误为空）。</summary>
    public string LastSaveError { get; private set; } = string.Empty;

    private readonly record struct RemovedEntry(ShortcutGroupViewModel Group, int OriginalIndex, ShortcutItem Model);

    private sealed class RemovedSlice
    {
        public required List<RemovedEntry> Entries { get; init; }
        public DateTime At { get; init; } = DateTime.UtcNow;
    }
}
