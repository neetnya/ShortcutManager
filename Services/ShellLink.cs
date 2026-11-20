using System.Runtime.InteropServices;

namespace ShortcutManager.Services;

/// <summary>解析 .lnk：读出目标路径和启动参数。失败时调用方继续把 .lnk 本身当条目。</summary>
internal static class ShellLink
{
    public static bool TryResolve(string lnkPath, out string target, out string arguments)
    {
        using var resolver = new ShellLinkResolver();
        return resolver.TryResolve(lnkPath, out target, out arguments);
    }

    /// <summary>可复用的 .lnk 解析会话：批量导入时只创建并释放一次 WScript.Shell COM 对象。</summary>
    internal sealed class ShellLinkResolver : IDisposable
    {
        private object? _shell;
        private bool _shellUnavailable;

        public bool TryResolve(string lnkPath, out string target, out string arguments)
        {
            target = string.Empty;
            arguments = string.Empty;
            if (_shellUnavailable
                || string.IsNullOrWhiteSpace(lnkPath)
                || !lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(lnkPath))
                return false;

            object? shortcut = null;
            try
            {
                if (_shell is null)
                {
                    var type = Type.GetTypeFromProgID("WScript.Shell");
                    if (type is null) { _shellUnavailable = true; return false; }
                    _shell = Activator.CreateInstance(type);
                    if (_shell is null) { _shellUnavailable = true; return false; }
                }

                dynamic dyn = _shell;
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
                if (shortcut is not null)
                    try { Marshal.FinalReleaseComObject(shortcut); } catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            if (_shell is not null)
            {
                try { Marshal.FinalReleaseComObject(_shell); } catch { /* ignore */ }
                _shell = null;
            }
        }
    }
}
