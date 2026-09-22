using System.Diagnostics;
using System.IO;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>双击时用系统默认程序打开文件 / 文件夹。</summary>
public static class Launcher
{
    public static void Open(ShortcutItem item, Action<string>? onError = null)
    {
        var fullPath = item.FullPath;
        try
        {
            if (Directory.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + fullPath + "\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
                });
                return;
            }

            onError?.Invoke($"找不到：{fullPath}");
        }
        catch (Exception ex)
        {
            onError?.Invoke($"打开失败：{ex.Message}");
        }
    }

    /// <summary>在资源管理器中定位并选中该项。</summary>
    public static void RevealInExplorer(ShortcutItem item)
    {
        var fullPath = item.FullPath;
        try
        {
            if (Directory.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + fullPath + "\"", UseShellExecute = true });
                return;
            }
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + fullPath + "\"", UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }
}
