namespace ShortcutManager.Services;

/// <summary>
/// 便携相对路径支持：
/// 目标在 exe 同目录或子目录时，config.json 里存相对路径（如 "tools\a.txt"），
/// 使用时再解析回完整路径。这样整个文件夹拷到别的机器/盘符后条目依然有效。
/// 目录之外的条目仍存绝对路径。
/// </summary>
public static class PathResolver
{
    /// <summary>程序所在目录（相对路径的基准）。</summary>
    public static string BaseDirectory { get; } =
        Path.GetFullPath(AppContext.BaseDirectory);

    /// <summary>完整路径 → 存储形式：exe 同目录或子目录转相对路径，否则保持绝对路径。</summary>
    public static string ToStoragePath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;

        string full;
        try
        {
            full = Path.GetFullPath(fullPath.Trim().Trim('"'));
        }
        catch
        {
            return fullPath;
        }

        // 同目录 → "file.txt"；子目录 → "sub\file.txt"；指向程序目录本身 → "."
        if (full.StartsWith(BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(BaseDirectory, full);
            if (rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return rel;
        }
        return full;
    }

    /// <summary>存储形式 → 完整路径：相对路径按 exe 目录解析，绝对路径原样返回。</summary>
    public static string Resolve(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return stored ?? string.Empty;
        var p = stored.Trim();
        if (Path.IsPathRooted(p)) return p;
        try
        {
            return Path.GetFullPath(Path.Combine(BaseDirectory, p));
        }
        catch
        {
            return p;
        }
    }

    /// <summary>把用户输入（可含引号、可为相对路径）规范化为完整路径；无法规范化时返回 null。</summary>
    public static string? NormalizeUserInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var p = input.Trim().Trim('"');
        if (p.Length == 0) return null;
        try
        {
            if (!Path.IsPathRooted(p))
                p = Path.Combine(BaseDirectory, p);
            if (p.Length == 2 && p[1] == ':') p += "\\";
            return Path.GetFullPath(p);
        }
        catch
        {
            return null;
        }
    }
}
