using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ShortcutManager.Services;

/// <summary>
/// 便携式 JSON 文件存储公共基类：优先保存在 exe 同目录，目录不可写时自动回退到 %APPDATA%\ShortcutManager。
/// 写入采用临时文件原子替换，读取损坏时自动备份为 .bak。
/// </summary>
public abstract class PortableJsonStore
{
    private static readonly ConcurrentDictionary<string, bool> WritableDirCache = new(StringComparer.OrdinalIgnoreCase);

    protected static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _fileName;

    public string FilePath { get; protected set; }
    public bool IsPortableLocation { get; protected set; }

    /// <summary>运行期间因便携目录写失败而回退到 %APPDATA% 时为 true。</summary>
    public bool FellBackToAppData { get; protected set; }

    protected PortableJsonStore(string fileName)
    {
        _fileName = fileName;
        var exeDir = AppContext.BaseDirectory;
        var portablePath = Path.Combine(exeDir, _fileName);

        if (CanWriteTo(exeDir))
        {
            FilePath = portablePath;
            IsPortableLocation = true;
        }
        else
        {
            var roamingPath = GetRoamingPath();
            Directory.CreateDirectory(Path.GetDirectoryName(roamingPath)!);
            FilePath = roamingPath;
            IsPortableLocation = false;
        }
    }

    protected string GetRoamingPath()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, "ShortcutManager", _fileName);
    }

    protected T? LoadJson<T>() where T : class
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(json, ReadOptions);
        }
        catch
        {
            // 文件损坏时备份，避免用户数据被直接覆盖丢失
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { /* ignore */ }
            return null;
        }
    }

    protected void SaveJson<T>(T data)
    {
        var json = JsonSerializer.Serialize(data, WriteOptions);

        try
        {
            WriteAtomic(FilePath, json);
            return;
        }
        catch (Exception) when (IsPortableLocation)
        {
            // 便携位置中途不可写或被占用 → 换到 %APPDATA% 重试一次
            WritableDirCache[AppContext.BaseDirectory] = false;
        }

        var roamingPath = GetRoamingPath();
        Directory.CreateDirectory(Path.GetDirectoryName(roamingPath)!);
        WriteAtomic(roamingPath, json);

        FilePath = roamingPath;
        IsPortableLocation = false;
        FellBackToAppData = true;
    }

    private static bool CanWriteTo(string dir)
    {
        return WritableDirCache.GetOrAdd(dir, static d =>
        {
            try
            {
                var probe = Path.Combine(d, $".sm_write_probe_{Guid.NewGuid():N}");
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                    fs.WriteByte(0);
                }
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static void WriteAtomic(string path, string json)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
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
