using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>
/// 便携式配置存储：数据文件优先放在 exe 同目录（config.json）。
/// 若该目录不可写（例如放在只读介质），自动回退到 %APPDATA%\ShortcutManager\config.json。
/// </summary>
public sealed class ConfigStore : PortableJsonStore
{
    public ConfigStore() : base("config.json") { }

    public List<ShortcutGroup> Load()
    {
        var data = LoadJson<List<ShortcutGroup>>();
        if (data is { Count: > 0 })
        {
            foreach (var g in data)
            {
                if (string.IsNullOrWhiteSpace(g.Id)) g.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(g.Name)) g.Name = "未命名分组";
                g.Items ??= new List<ShortcutItem>();
                g.Items.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Path));
            }
            return data;
        }

        return new List<ShortcutGroup> { new() { Name = "常用" } };
    }

    public void Save(IEnumerable<ShortcutGroup> groups) => SaveJson(groups.ToList());
}
