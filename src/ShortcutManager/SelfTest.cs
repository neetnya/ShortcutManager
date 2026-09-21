using System.Windows.Media;
using ShortcutManager.Models;
using ShortcutManager.Services;

namespace ShortcutManager;

/// <summary>命令行自检：验证图标提取与配置读写，不依赖 UI。</summary>
internal static class SelfTest
{
    [STAThread]
    public static int Run()
    {
        // WinExe 没有控制台，结果写到文件里
        var log = new List<string>();
        void W(string s) { log.Add(s); }

        var targets = new (string Path, bool IsDir)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.System), true),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), false),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), false),
            (Path.Combine(Path.GetTempPath(), "sm-test-data", "readme.txt"), false),
            (@"C:\definitely\missing\thing.txt", false),
        };

        var ok = 0;
        foreach (var (path, isDir) in targets)
        {
            ImageSource? src = null;
            var err = "";
            try { src = ShellIcons.Get(path, isDir); }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

            if (src is null)
                W($"FAIL  {path}  -> null  {err}");
            else
                W($"OK    {path}  -> {src.Width}x{src.Height} {src.GetType().Name}");
            if (src is not null) ok++;
        }

        // 配置读写往返
        try
        {
            var store = new ConfigStore();
            W($"config: {store.FilePath} (portable={store.IsPortableLocation})");
            var groups = store.Load();
            W($"loaded groups: {groups.Count}, items: {groups.Sum(g => g.Items.Count)}");
            ok++;
        }
        catch (Exception ex)
        {
            W("config FAIL: " + ex);
        }

        // 显示名规则
        var f = new ShortcutItem { Path = @"C:\a\b\report.final.docx", IsDirectory = false };
        var d = new ShortcutItem { Path = @"C:\a\b\MyFolder", IsDirectory = true };
        W($"name(file) = '{f.EffectiveName}'  (expect 'report.final')");
        W($"name(dir)  = '{d.EffectiveName}'  (expect 'MyFolder')");
        if (f.EffectiveName == "report.final" && d.EffectiveName == "MyFolder") ok++;
        else W("FAIL name rules");

        W($"RESULT {ok}/{targets.Length + 2}");
        var total = targets.Length + 2;

        var outPath = Path.Combine(AppContext.BaseDirectory, "selftest.log");
        try { File.WriteAllLines(outPath, log, System.Text.Encoding.UTF8); } catch { /* ignore */ }

        return ok == total ? 0 : 1;
    }
}
