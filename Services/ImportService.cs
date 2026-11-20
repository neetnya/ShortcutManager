using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>从文件系统路径构建条目，并按需预热图标缓存。</summary>
public static class ImportService
{
    /// <summary>把一批拖入/选择的路径展开为条目（跳过重复项与不存在的项）。
    /// 位于程序目录内的目标会以相对路径存储，方便整个文件夹迁移。</summary>
    public static List<ShortcutItem> CreateItems(IEnumerable<string> paths, ShortcutGroup target)
    {
        var existing = new HashSet<string>(target.Items.Select(i => i.PathKey), StringComparer.OrdinalIgnoreCase);
        var result = new List<ShortcutItem>();
        using var lnkResolver = new ShellLink.ShellLinkResolver();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = raw.Trim().Trim('"').TrimEnd('\\', '/');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            if (path.Length == 0) continue;

            bool isDir = Directory.Exists(path);
            bool isFile = !isDir && File.Exists(path);
            if (!isDir && !isFile) continue;

            var arguments = string.Empty;
            if (!isDir && lnkResolver.TryResolve(path, out var resolved, out var args)
                && (Directory.Exists(resolved) || File.Exists(resolved)))
            {
                path = resolved;
                arguments = args;
                isDir = Directory.Exists(resolved);
            }

            var key = ShortcutItem.NormalizePath(path);
            if (!existing.Add(key)) continue;

            result.Add(new ShortcutItem
            {
                Path = PathResolver.ToStoragePath(path),
                DisplayName = string.Empty,
                IsDirectory = isDir,
                Arguments = arguments,
            });
        }

        return result;
    }

    /// <summary>将条目放入后台图标提取队列进行预热，避免首屏图标逐个跳出或并发争锁。</summary>
    public static void WarmIcons(IEnumerable<ShortcutItem> items)
    {
        foreach (var item in items)
        {
            ShellIcons.EnqueueWarm(item.FullPath, item.IsDirectory);
        }
    }
}
