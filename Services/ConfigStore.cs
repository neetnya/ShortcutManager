using System.Text;
using System.Text.Json;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>
/// 便携式配置存储：数据文件优先放在 exe 同目录（config.json）。
/// 若该目录不可写（例如放在只读介质），自动回退到 %APPDATA%\ShortcutManager\config.json。
/// </summary>
public sealed class ConfigStore
{
    private const string FileName = "config.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string FilePath { get; private set; }
    public bool IsPortableLocation { get; private set; }

    /// <summary>已回退到 %APPDATA% 时为 true，界面上可以据此提示用户。</summary>
    public bool FellBackToAppData { get; private set; }

    private static string RoamingPath()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, "ShortcutManager", FileName);
    }

    public ConfigStore()
    {
        var exeDir = AppContext.BaseDirectory;
        var portablePath = Path.Combine(exeDir, FileName);

        if (CanWriteTo(exeDir))
        {
            FilePath = portablePath;
            IsPortableLocation = true;
        }
        else
        {
            var dir = Path.GetDirectoryName(RoamingPath())!;
            Directory.CreateDirectory(dir);
            FilePath = RoamingPath();
            IsPortableLocation = false;
        }
    }

    private static bool CanWriteTo(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".sm_write_probe");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public List<ShortcutGroup> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var data = JsonSerializer.Deserialize<List<ShortcutGroup>>(json, ReadOptions);
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
            }
        }
        catch
        {
            // 配置损坏时静默回退到默认分组，并把损坏文件备份，避免用户数据被直接覆盖丢失
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { /* ignore */ }
        }

        return new List<ShortcutGroup> { new() { Name = "常用" } };
    }

    /// <summary>
    /// 原子写入：先写临时文件再替换，避免写入过程中断电导致配置损坏。
    /// 如果便携位置写入失败（目录中途变成只读、被占用等），自动回退到 %APPDATA% 再写一次，
    /// 避免用户后续所有改动静默丢失。
    /// </summary>
    public void Save(IEnumerable<ShortcutGroup> groups)
    {
        var json = JsonSerializer.Serialize(groups.ToList(), WriteOptions);

        try
        {
            WriteAtomic(FilePath, json);
            return;
        }
        catch (Exception) when (IsPortableLocation)
        {
            // 便携位置写不进去 → 换到 %APPDATA% 重试一次
        }

        var dir = Path.GetDirectoryName(RoamingPath())!;
        Directory.CreateDirectory(dir);
        WriteAtomic(RoamingPath(), json);

        FilePath = RoamingPath();
        IsPortableLocation = false;
        FellBackToAppData = true;
    }

    private static void WriteAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            // 失败时别把半成品 .tmp 留在目录里（便携软件目录应该保持干净）
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }
}
