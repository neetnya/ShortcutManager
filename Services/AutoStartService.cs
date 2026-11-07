using Microsoft.Win32;

namespace ShortcutManager.Services;

/// <summary>
/// 开机自启：写入当前用户的 Run 注册表键（HKCU，无需管理员权限）。
/// 便携软件自启时始终指向当前 exe 的完整路径（拷走/换位置后需要重新开关一次以刷新路径）。
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShortcutManager";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string v && v.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置自启；返回是否成功。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
