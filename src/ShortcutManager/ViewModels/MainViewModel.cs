using System.Collections.ObjectModel;
using ShortcutManager.Models;
using ShortcutManager.Services;

namespace ShortcutManager.ViewModels;

/// <summary>整个应用的状态。</summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>一行固定四个。</summary>
    public const int Columns = 4;

    /// <summary>网格区域左右内边距（与 XAML 中 ListBox 的 Padding 保持一致）。</summary>
    public const double GridPadding = 8;

    /// <summary>每格间距（与 Tile 样式的 Margin 保持一致）。</summary>
    public const double TileGap = 6;

    /// <summary>每格高度（与 Tile 样式保持一致）。</summary>
    public const double TileHeight = 92;

    private readonly ConfigStore _store;
    private ShortcutGroupViewModel? _selectedGroup;
    private ShortcutViewModel? _selectedItem;
    private ShortcutViewModel? _clipboardItem;
    private string _statusText = string.Empty;

    public MainViewModel(ConfigStore store, List<ShortcutGroup> groups)
    {
        _store = store;
        foreach (var g in groups)
        {
            // 载入时根据磁盘实际情况修正 文件/文件夹 判定
            foreach (var item in g.Items)
                item.IsDirectory = Directory.Exists(item.Path);

            Groups.Add(new ShortcutGroupViewModel(g));
        }

        _selectedGroup = Groups.FirstOrDefault();
    }

    public ObservableCollection<ShortcutGroupViewModel> Groups { get; } = new();

    public ShortcutGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (Set(ref _selectedGroup, value))
            {
                foreach (var g in Groups) g.NotifyCountChanged();
                Raise(nameof(SelectedItems));
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

    public bool MoveGroup(ShortcutGroupViewModel group, int delta)
    {
        var i = Groups.IndexOf(group);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Groups.Count) return false;
        Groups.Move(i, j);
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
            target.Items.Add(new ShortcutViewModel(item, target));
        }
        target.NotifyCountChanged();
        Save();
        WarmIconsAsync(target);
        return created.Count;
    }

    public void RemoveItem(ShortcutViewModel item)
    {
        var group = item.Owner;
        group.Model.Items.Remove(item.Model);
        group.Items.Remove(item);
        group.NotifyCountChanged();
        if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
        Save();
    }

    /// <summary>把条目移动到其他分组（追加到末尾）。</summary>
    public bool MoveItemToGroup(ShortcutViewModel item, ShortcutGroupViewModel target)
    {
        if (ReferenceEquals(item.Owner, target)) return false;

        var source = item.Owner;
        source.Model.Items.Remove(item.Model);
        source.Items.Remove(item);
        source.NotifyCountChanged();

        var model = item.Model;
        if (target.Model.Items.Any(i => i.PathKey == model.PathKey))
        {
            StatusText = $"“{target.Name}”中已存在该项";
        }
        else
        {
            target.Model.Items.Add(model);
            target.Items.Add(new ShortcutViewModel(model, target));
            target.NotifyCountChanged();
        }

        if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
        Save();
        return true;
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

    public void CopyItem(ShortcutViewModel item) => _clipboardItem = item;

    public ShortcutViewModel? ClipboardItem => _clipboardItem;

    /// <summary>重新读取所有条目的图标与存在状态。</summary>
    public void RefreshAll()
    {
        foreach (var g in Groups)
        {
            g.NotifyCountChanged();
            foreach (var item in g.Items)
            {
                item.Model.IsDirectory = Directory.Exists(item.Model.Path);
                item.Refresh();
            }
        }
        Save();
    }

    public void WarmIcons(ShortcutGroupViewModel group) => WarmIconsAsync(group);

    private void WarmIconsAsync(ShortcutGroupViewModel group)
    {
        var snapshot = group.Items.Select(i => i.Model).ToList();
        Task.Run(() => ImportService.WarmIcons(snapshot));
    }

    public void Save()
    {
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
            // 两个位置都写不进去时才报错（例如磁盘满、权限被策略限制）
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

    private bool _warnedFallback;

    /// <summary>调试用：最近一次保存的错误（无错误为空）。</summary>
    public string LastSaveError { get; private set; } = string.Empty;
}
