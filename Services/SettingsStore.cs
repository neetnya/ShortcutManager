using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>
/// 应用设置的便携式存储：独立于 config.json，存为 exe 同目录的 settings.json。
/// 目录不可写时回退到 %APPDATA%\ShortcutManager\settings.json。
/// 读取损坏或缺失时回退到默认设置；写入走原子替换。
/// </summary>
public sealed class SettingsStore : PortableJsonStore
{
    public SettingsStore() : base("settings.json") { }

    public AppSettings Load()
    {
        var data = LoadJson<AppSettings>();
        if (data is not null)
        {
            data.Normalize();
            return data;
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings) => SaveJson(settings);
}
