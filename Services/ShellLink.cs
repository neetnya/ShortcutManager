using System.Runtime.InteropServices;

namespace ShortcutManager.Services;

/// <summary>解析 .lnk：读出目标路径和启动参数。失败时调用方继续把 .lnk 本身当条目。</summary>
internal static class ShellLink
{
    public static bool TryResolve(string lnkPath, out string target, out string arguments)
    {
        target = string.Empty;
        arguments = string.Empty;
        if (string.IsNullOrWhiteSpace(lnkPath)
            || !lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(lnkPath))
            return false;

        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            shell = Activator.CreateInstance(type);
            if (shell is null) return false;

            dynamic dyn = shell;
            shortcut = dyn.CreateShortcut(lnkPath);
            if (shortcut is null) return false;

            dynamic sc = shortcut;
            target = (string)(sc.TargetPath ?? "");
            arguments = (string)(sc.Arguments ?? "");
            return !string.IsNullOrWhiteSpace(target);
        }
        catch
        {
            target = string.Empty;
            arguments = string.Empty;
            return false;
        }
        finally
        {
            if (shortcut is not null) try { Marshal.FinalReleaseComObject(shortcut); } catch { /* ignore */ }
            if (shell is not null) try { Marshal.FinalReleaseComObject(shell); } catch { /* ignore */ }
        }
    }
}
