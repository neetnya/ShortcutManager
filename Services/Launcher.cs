using System.Diagnostics;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>双击或右键菜单时用系统默认程序/资源管理器打开文件或文件夹。</summary>
public static class Launcher
{
    /// <summary>
    /// 同步打开。返回 true = 已把启动请求交给系统；false = 失败（错误文案通过 <paramref name="onError"/> 回传）。
    /// 注意：不要在 UI 线程直接调它，界面里的操作请走 <see cref="OpenAsync"/>。
    /// </summary>
    public static bool Open(ShortcutItem item, Action<string>? onError = null)
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
                return true;
            }

            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullPath,
                    Arguments = item.Arguments ?? string.Empty,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
                });
                return true;
            }

            onError?.Invoke($"找不到：{fullPath}");
            return false;
        }
        catch (Exception ex)
        {
            onError?.Invoke($"打开失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 异步打开，不占 UI 线程：把 <see cref="Open"/> 放到独立的 STA 线程上跑。
    /// 返回 null 表示成功，否则是可直接展示的错误文案。
    /// </summary>
    public static Task<string?> OpenAsync(ShortcutItem item)
        => RunOnStaThread(() =>
        {
            string? error = null;
            return Open(item, msg => error = msg) ? null : (error ?? $"打开失败：{item.FullPath}");
        });

    /// <summary>在资源管理器中定位并选中该项（同步）。</summary>
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

    /// <summary>在资源管理器中定位并选中该项（异步，不阻塞 UI 线程）。</summary>
    public static Task RevealInExplorerAsync(ShortcutItem item)
        => Task.Run(() => RevealInExplorer(item));

    /// <summary>打开条目所在的父目录（异步，不阻塞 UI 线程）。返回 null 表示成功，否则返回错误提示。</summary>
    public static Task<string?> OpenParentDirectoryAsync(ShortcutItem item)
        => Task.Run<string?>(() =>
        {
            var fullPath = item.FullPath;
            var parent = item.IsDirectory
                ? Directory.GetParent(fullPath)?.FullName
                : Path.GetDirectoryName(fullPath);

            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                return "找不到所在目录";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + parent + "\"",
                    UseShellExecute = true,
                });
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ShortcutManager.Open",
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        catch (PlatformNotSupportedException)
        {
            // 非 Windows 上退化为默认公寓
        }

        thread.Start();
        return tcs.Task;
    }
}
