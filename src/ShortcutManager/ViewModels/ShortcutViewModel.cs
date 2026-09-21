using System.Windows.Media;
using ShortcutManager.Models;
using ShortcutManager.Services;

namespace ShortcutManager.ViewModels;

/// <summary>网格里的一格。</summary>
public sealed class ShortcutViewModel : ObservableObject
{
    private readonly ShortcutItem _model;
    private ImageSource? _icon;
    private bool _isRenaming;
    private string _renameBuffer = string.Empty;
    private bool _isSelected;
    private bool _isDragTarget;
    private bool _isDragTargetAfter;

    public ShortcutViewModel(ShortcutItem model, ShortcutGroupViewModel owner)
    {
        _model = model;
        Owner = owner;
    }

    public ShortcutItem Model => _model;

    public ShortcutGroupViewModel Owner { get; }

    public string Path => _model.Path;

    public bool IsDirectory => _model.IsDirectory;

    public bool Exists => _model.Exists;

    public string Name => _model.EffectiveName;

    public ImageSource? Icon
    {
        get
        {
            if (_icon is null)
            {
                _icon = ShellIcons.Get(_model.Path, _model.IsDirectory);
                Raise();
            }
            return _icon;
        }
    }

    public string Tooltip
    {
        get
        {
            var kind = IsDirectory ? "文件夹" : "文件";
            return $"{Name}\n[{kind}] {Path}" + (Exists ? string.Empty : "\n（路径不存在）");
        }
    }

    public bool IsMissing => !Exists;

    /// <summary>是否处于就地重命名编辑状态。</summary>
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

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>拖拽时作为插入点高亮（插到该格左侧）。</summary>
    public bool IsDragTarget
    {
        get => _isDragTarget;
        set => Set(ref _isDragTarget, value);
    }

    /// <summary>拖拽时作为插入点高亮（插到该格右侧）。</summary>
    public bool IsDragTargetAfter
    {
        get => _isDragTargetAfter;
        set => Set(ref _isDragTargetAfter, value);
    }

    public void ClearDragMarker()
    {
        if (_isDragTarget) IsDragTarget = false;
        if (_isDragTargetAfter) IsDragTargetAfter = false;
    }

    public void BeginRename()
    {
        RenameBuffer = Name;
        IsRenaming = true;
    }

    public void CancelRename() => IsRenaming = false;

    /// <summary>提交重命名；名字为空则回退为默认名。</summary>
    public void CommitRename()
    {
        var text = (RenameBuffer ?? string.Empty).Trim();
        _model.DisplayName = string.Equals(text, _model.DefaultDisplayName, StringComparison.Ordinal) ? string.Empty : text;
        IsRenaming = false;
        Raise(nameof(Name));
        Raise(nameof(Tooltip));
    }

    /// <summary>刷新路径相关的显示（路径不存在状态变化时调用）。</summary>
    public void Refresh()
    {
        _icon = null;
        Raise(nameof(Icon));
        Raise(nameof(Name));
        Raise(nameof(Tooltip));
        Raise(nameof(Exists));
        Raise(nameof(IsMissing));
    }
}
