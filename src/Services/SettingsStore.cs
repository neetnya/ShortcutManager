using System.Text;
using System.Text.Json;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>
/// 应用设置的便携式存储：独立于 config.json，存为 exe 同目录的 settings.json。
/// 目录不可写时回退到 %APPDATA%\ShortcutManager\settings.json。
/// 读取损坏或缺失时回退到默认设置；写入走原子替换。
/// </summary>
public sealed class SettingsStore
{
    private const string FileName = "settings.json";

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

    public SettingsStore()
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
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(roaming, "ShortcutManager");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, FileName);
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

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var data = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
                if (data is not null)
                {
                    Sanitize(data);
                    return data;
                }
            }
        }
        catch
        {
            // 损坏时备份并回退默认，不覆盖用户文件
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { /* ignore */ }
        }

        return new AppSettings();
    }

    /// <summary>归一化非法值（例如未知的修饰键、无效的虚拟键码），避免注册快捷键时异常。</summary>
    private static void Sanitize(AppSettings s)
    {
        var valid = (int)(HotkeyModifiers.Alt | HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Win);
        s.HotkeyModifiers &= valid;

        if (s.HotkeyKey <= 0 || s.HotkeyKey > 0xFF) s.HotkeyKey = 0x51; // Q
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, WriteOptions);

        try
        {
            WriteAtomic(FilePath, json);
            return;
        }
        catch (Exception) when (IsPortableLocation)
        {
            // 便携位置写不进去 → 换 %APPDATA% 再试一次
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(roaming, "ShortcutManager");
        Directory.CreateDirectory(dir);
        WriteAtomic(Path.Combine(dir, FileName), json);

        FilePath = Path.Combine(dir, FileName);
        IsPortableLocation = false;
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
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }
}
