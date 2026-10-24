using System.Diagnostics;
using System.IO;
using ShortcutManager.Models;

namespace ShortcutManager.Services;

/// <summary>双击时用系统默认程序打开文件 / 文件夹。</summary>
public static class Launcher
{
    /// <summary>
    /// 同步打开。返回 true = 已把启动请求交给系统；false = 失败（错误文案通过 <paramref name="onError"/> 回传）。
    /// <para>
    /// 注意：ShellExecuteEx 在目标走 DDE（Office / Acrobat 等）、文件关联缺失要查注册表、
    /// 或首次加载 shell 扩展时会阻塞几百毫秒甚至更久。**不要**在 UI 线程直接调它，
    /// 界面里的双击打开请走 <see cref="OpenAsync"/>。
    /// </para>
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
    /// 异步打开，不占 UI 线程：把 <see cref="Open"/> 放到一条独立的 STA 线程上跑
    /// （与 UI 线程同公寓类型，行为和以前完全一致，只是不再阻塞界面）。
    /// 返回 null 表示成功，否则是可直接展示的错误文案。
    /// </summary>
    public static Task<string?> OpenAsync(ShortcutItem item)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            string? error = null;
            try
            {
                if (Open(item, msg => error = msg)) tcs.TrySetResult(null);
                else tcs.TrySetResult(error ?? $"打开失败：{item.FullPath}");
            }
            catch (Exception ex)
            {
                tcs.TrySetResult($"打开失败：{ex.Message}");
            }
        })
        {
            IsBackground = true,          // 线程不拖住进程退出
            Name = "ShortcutManager.Open",
        };

        try
        {
            // ShellExecuteEx 内部会用到 STA 的 shell COM，跟 UI 线程保持一致最稳
            thread.SetApartmentState(ApartmentState.STA);
        }
        catch (PlatformNotSupportedException)
        {
            // 非 Windows 上退化为默认公寓，不影响其它平台编译
        }

        thread.Start();
        return tcs.Task;
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
