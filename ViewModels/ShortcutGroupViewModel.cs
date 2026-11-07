using System.Collections.ObjectModel;
using ShortcutManager.Models;

namespace ShortcutManager.ViewModels;

/// <summary>一个 tab = 一个分组，内部固定“一行三个”。</summary>
public sealed class ShortcutGroupViewModel : ObservableObject
{
    private string _name;
    private bool _isRenaming;
    private string _renameBuffer = string.Empty;
    private bool _isDragTarget;
    private bool _isDragTargetAfter;
    private bool _isDropHover;

    public ShortcutGroupViewModel(ShortcutGroup model)
    {
        Model = model;
        _name = model.Name;
        foreach (var item in model.Items)
            Items.Add(new ShortcutViewModel(item, this));
    }

    public ShortcutGroup Model { get; }

    public ObservableCollection<ShortcutViewModel> Items { get; } = new();

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { Model.Name = value; } }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (Set(ref _isRenaming, value)) Raise(nameof(IsNotRenaming)); }
    }

    public bool IsNotRenaming => !_isRenaming;

    public string RenameBuffer
    {
        get => _renameBuffer;
        set => Set(ref _renameBuffer, value);
    }

    public string TabTitle => $"{Name} ({Items.Count})";

    public void NotifyCountChanged() => Raise(nameof(TabTitle));

    /// <summary>拖拽时作为插入点高亮（插到该标签左侧）。</summary>
    public bool IsDragTarget
    {
        get => _isDragTarget;
        set => Set(ref _isDragTarget, value);
    }

    /// <summary>拖拽时作为插入点高亮（插到该标签右侧）。</summary>
    public bool IsDragTargetAfter
    {
        get => _isDragTargetAfter;
        set => Set(ref _isDragTargetAfter, value);
    }

    /// <summary>磁贴或文件拖到该标签上（移入/导入该组）时整颗高亮。</summary>
    public bool IsDropHover
    {
        get => _isDropHover;
        set => Set(ref _isDropHover, value);
    }

    public void ClearDragMarker()
    {
        if (_isDragTarget) IsDragTarget = false;
        if (_isDragTargetAfter) IsDragTargetAfter = false;
        if (_isDropHover) IsDropHover = false;
    }

    public void BeginRename()
    {
        RenameBuffer = Name;
        IsRenaming = true;
    }

    public void CommitRename()
    {
        var text = (RenameBuffer ?? string.Empty).Trim();
        Name = string.IsNullOrEmpty(text) ? "未命名分组" : text;
        IsRenaming = false;
    }
}
