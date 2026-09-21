using System.Windows.Media;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>从文件系统路径构建条目，并按需预热图标缓存。</summary>
public static class ImportService
{
    /// <summary>把一批拖入/选择的路径展开为条目（跳过重复项与不存在的项）。</summary>
    public static List<ShortcutItem> CreateItems(IEnumerable<string> paths, ShortcutGroup target)
    {
        var existing = new HashSet<string>(target.Items.Select(i => i.PathKey), StringComparer.OrdinalIgnoreCase);
        var result = new List<ShortcutItem>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = raw.Trim().Trim('"').TrimEnd('\\', '/');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            if (path.Length == 0) continue;

            bool isDir = Directory.Exists(path);
            bool isFile = File.Exists(path);
            if (!isDir && !isFile) continue;

            var key = ShortcutItem.NormalizePath(path);
            if (!existing.Add(key)) continue;

            result.Add(new ShortcutItem { Path = path, DisplayName = string.Empty, IsDirectory = isDir });
        }

        return result;
    }

    /// <summary>后台预热图标，避免首屏出现图标逐个跳出的观感。</summary>
    public static void WarmIcons(IEnumerable<ShortcutItem> items)
    {
        foreach (var item in items)
        {
            try { ShellIcons.Get(item.Path, item.IsDirectory); }
            catch { /* ignore */ }
        }
    }
}
