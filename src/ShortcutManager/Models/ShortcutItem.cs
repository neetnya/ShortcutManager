using System.Text.Json.Serialization;

namespace ShortcutManager.Models;

/// <summary>一个快捷方式条目：一个文件或一个文件夹。</summary>
public sealed class ShortcutItem
{
    /// <summary>完整路径。文件夹不带结尾反斜杠。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>显示名。为空时回退到文件名（不含扩展名）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>磁盘上缓存的图标 png 文件名（仅缓存时使用），可为空。</summary>
    public string? CachedIcon { get; set; }

    [JsonIgnore]
    public bool IsDirectory { get; set; }

    [JsonIgnore]
    public bool Exists => !string.IsNullOrWhiteSpace(Path) && (File.Exists(Path) || Directory.Exists(Path));

    /// <summary>默认显示名：文件夹名 / 不含扩展名的文件名。</summary>
    [JsonIgnore]
    public string DefaultDisplayName
    {
        get
        {
            var p = Path.TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(p);
            if (string.IsNullOrEmpty(name)) name = p;
            if (!IsDirectory && !string.IsNullOrEmpty(name))
            {
                var ext = System.IO.Path.GetExtension(name);
                if (!string.IsNullOrEmpty(ext)) name = name[..^ext.Length];
            }
            return name;
        }
    }

    [JsonIgnore]
    public string EffectiveName => string.IsNullOrWhiteSpace(DisplayName) ? DefaultDisplayName : DisplayName;

    /// <summary>用于比较的规范化路径键（忽略大小写与结尾分隔符）。</summary>
    [JsonIgnore]
    public string PathKey => NormalizePath(Path);

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Trim().TrimEnd('\\', '/');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p.ToLowerInvariant();
    }

    public ShortcutItem Clone() => new() { Path = Path, DisplayName = DisplayName, CachedIcon = CachedIcon };
}
