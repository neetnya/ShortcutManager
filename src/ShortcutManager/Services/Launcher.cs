using System.Diagnostics;
using System.IO;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>双击时用系统默认程序打开文件 / 文件夹。</summary>
public static class Launcher
{
    public static void Open(ShortcutItem item, Action<string>? onError = null)
    {
        try
        {
            if (Directory.Exists(item.Path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + item.Path + "\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (File.Exists(item.Path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.Path,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(item.Path) ?? string.Empty,
                });
                return;
            }

            onError?.Invoke($"找不到：{item.Path}");
        }
        catch (Exception ex)
        {
            onError?.Invoke($"打开失败：{ex.Message}");
        }
    }

    /// <summary>在资源管理器中定位并选中该项。</summary>
    public static void RevealInExplorer(ShortcutItem item)
    {
        try
        {
            if (Directory.Exists(item.Path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + item.Path + "\"", UseShellExecute = true });
                return;
            }
            if (File.Exists(item.Path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + item.Path + "\"", UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }
}
