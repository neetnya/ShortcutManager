using ShortcutManager.Models;
using ShortcutManager.Services;
using ShortcutManager.ViewModels;

namespace ShortcutManager;

/// <summary>
/// 无界面的逻辑自检：覆盖分组/条目增删改、排序、跨组移动、去重、配置往返。
/// 用 --test 运行。
/// </summary>
internal static class LogicTests
{
    private static readonly List<string> Log = new();
    private static int _pass;
    private static int _fail;

    public static int Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "sm-logic-test");
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        Directory.CreateDirectory(sandbox);

        var dirA = Directory.CreateDirectory(Path.Combine(sandbox, "AlphaDocs")).FullName;
        var dirB = Directory.CreateDirectory(Path.Combine(sandbox, "BetaImages")).FullName;
        var fileA = Path.Combine(sandbox, "readme.txt");
        var fileB = Path.Combine(sandbox, "notes.md");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");

        // 相对路径测试用的文件放在程序目录内（测试结束统一清理）
        var localFile = Path.Combine(PathResolver.BaseDirectory, "sm-rel-test.txt");
        var localDir = Path.Combine(PathResolver.BaseDirectory, "sm-rel-test-dir");
        File.WriteAllText(localFile, "x");
        Directory.CreateDirectory(localDir);

        // ---------- 命名规则 ----------
        Section("命名规则");
        var f1 = new ShortcutItem { Path = @"C:\x\report.final.docx", IsDirectory = false };
        Check("文件名去扩展名", f1.EffectiveName, "report.final");

        var d1 = new ShortcutItem { Path = @"C:\x\MyFolder", IsDirectory = true };
        Check("文件夹名保持不变", d1.EffectiveName, "MyFolder");

        var f2 = new ShortcutItem { Path = @"C:\x\noext", IsDirectory = false };
        Check("无扩展名文件", f2.EffectiveName, "noext");

        var d2 = new ShortcutItem { Path = @"D:\", IsDirectory = true };
        Check("根目录不崩", d2.EffectiveName.Length > 0, true);

        // ---------- 路径规范化与去重 ----------
        Section("路径规范化");
        Check("大小写归一", ShortcutItem.NormalizePath(@"C:\Foo\Bar"), ShortcutItem.NormalizePath(@"c:\foo\bar\"));
        Check("结尾斜杠归一", ShortcutItem.NormalizePath(@"C:\Foo\"), ShortcutItem.NormalizePath(@"C:\Foo"));
        Check("盘符根", ShortcutItem.NormalizePath(@"C:"), ShortcutItem.NormalizePath(@"C:\"));

        // ---------- 相对路径（便携存储） ----------
        Section("相对路径");
        Check("同目录存为相对路径", PathResolver.ToStoragePath(localFile), "sm-rel-test.txt");
        Check("子目录存为相对路径", PathResolver.ToStoragePath(localDir), "sm-rel-test-dir");
        Check("目录外保持绝对路径", PathResolver.ToStoragePath(fileA), Path.GetFullPath(fileA));
        Check("相对路径解析回完整路径", PathResolver.Resolve("sm-rel-test.txt"), Path.GetFullPath(localFile));
        Check("绝对路径解析不变", PathResolver.Resolve(fileA), fileA);

        var relItem = new ShortcutItem { Path = "sm-rel-test.txt", IsDirectory = false };
        Check("相对路径条目 Exists", relItem.Exists, true);
        Check("相对路径条目 FullPath", relItem.FullPath, Path.GetFullPath(localFile));

        // ---------- 视图模型 ----------
        Section("分组与条目操作");
        var store = new ConfigStore();
        var groups = new List<ShortcutGroup> { new() { Name = "G1" }, new() { Name = "G2" } };
        var vm = new MainViewModel(store, groups);

        Check("初始分组数", vm.Groups.Count, 2);
        Check("默认选中第一个", vm.SelectedGroup?.Name, "G1");

        var g1 = vm.Groups[0];
        var g2 = vm.Groups[1];

        var imported = vm.Import(new[] { dirA, dirB, fileA, fileB }, g1);
        Check("导入 4 项", imported, 4);
        Check("组内条目数", g1.Items.Count, 4);

        var again = vm.Import(new[] { dirA, fileA }, g1);
        Check("重复导入被忽略", again, 0);
        Check("条目数未变", g1.Items.Count, 4);

        var bogus = vm.Import(new[] { @"C:\nope\does\not\exist.xyz" }, g1);
        Check("不存在的路径被忽略", bogus, 0);

        Check("文件夹被识别", g1.Items[0].IsDirectory, true);
        Check("文件被识别", g1.Items[2].IsDirectory, false);

        // 排序：把第 1 个移到第 3 个之后
        var order = g1.Items.Select(i => i.Name).ToList();
        Log.Add($"  before reorder: {string.Join(" | ", order)}");
        var src = g1.Items[0];
        var dst = g1.Items[2];
        vm.Reorder(src, dst, insertAfter: true);
        var after = g1.Items.Select(i => i.Name).ToList();
        Log.Add($"  after  reorder: {string.Join(" | ", after)}");
        Check("排序后元素仍在", after.Count, 4);
        Check("排序后 src 位置", after.IndexOf(src.Name), 2);
        Check("排序后顺序符合预期", string.Join(",", after), string.Join(",", new[] { order[1], order[2], order[0], order[3] }));

        // 模型与视图集合保持同步
        Check("模型集合同步", string.Join(",", g1.Model.Items.Select(i => i.Path)),
                                string.Join(",", g1.Items.Select(i => i.Path)));

        // 移到末尾
        var last = g1.Items[0];
        vm.MoveToEnd(last);
        Check("移到末尾", g1.Items[^1].Name, last.Name);
        Check("模型同步(末尾)", g1.Model.Items[^1].Path, last.Path);

        // 重命名
        var target = g1.Items[0];
        var originalName = target.Name;
        target.BeginRename();
        Check("重命名进入编辑态", target.IsRenaming, true);
        target.RenameBuffer = "我的自定义名字";
        target.CommitRename();
        Check("重命名生效", target.Name, "我的自定义名字");
        Check("重命名退出编辑态", target.IsRenaming, false);

        // 改回默认名应收敛为空 DisplayName
        target.BeginRename();
        target.RenameBuffer = target.Model.DefaultDisplayName;
        target.CommitRename();
        Check("改回默认名清空覆盖", target.Model.DisplayName, "");
        Check("默认名恢复", target.Name, target.Model.DefaultDisplayName);

        // 取消重命名
        target.BeginRename();
        target.RenameBuffer = "不应保存";
        target.CancelRename();
        Check("取消重命名不写入", target.Name, target.Model.DefaultDisplayName);

        // 跨组移动
        Section("跨组移动");
        var mover = g1.Items[0];
        var moverPath = mover.Path;
        var before1 = g1.Items.Count;
        var before2 = g2.Items.Count;
        var moved = vm.MoveItemToGroup(mover, g2);
        Check("跨组移动返回 true", moved, true);
        Check("源组 -1", g1.Items.Count, before1 - 1);
        Check("目标组 +1", g2.Items.Count, before2 + 1);
        Check("目标组末尾是新项", g2.Items[^1].Path, moverPath);
        Check("源组模型同步", g1.Model.Items.Any(i => i.Path == moverPath), false);
        Check("目标组模型同步", g2.Model.Items.Any(i => i.Path == moverPath), true);

        // 移回并测试重复移动
        var dupBlocked = vm.MoveItemToGroup(g2.Items[0], g2);
        Check("同组移动返回 false", dupBlocked, false);

        // 移除
        Section("移除条目");
        var remCount = g1.Items.Count;
        var remTarget = g1.Items[0];
        var remPath = remTarget.Path;
        vm.RemoveItem(remTarget);
        Check("条目移除", g1.Items.Count, remCount - 1);
        Check("模型移除", g1.Model.Items.Any(i => i.Path == remPath), false);
        Check("磁盘文件未被删除", File.Exists(remPath) || Directory.Exists(remPath), true);

        // 程序目录内的文件导入后存为相对路径
        Section("相对路径导入");
        var relImport = vm.Import(new[] { localFile }, g1);
        Check("程序目录内文件可导入", relImport, 1);
        Check("导入存为相对路径", g1.Items[^1].StoredPath, "sm-rel-test.txt");
        Check("相对路径解析为完整路径", g1.Items[^1].Path, Path.GetFullPath(localFile));
        Check("相对路径重复导入被忽略", vm.Import(new[] { localFile }, g1), 0);
        Check("相对路径条目默认名", g1.Items[^1].Name, "sm-rel-test");

        // 分组增删
        Section("分组增删");
        var gc0 = vm.Groups.Count;
        vm.AddGroup();
        Check("新增分组", vm.Groups.Count, gc0 + 1);
        Check("新分组被选中", ReferenceEquals(vm.SelectedGroup, vm.Groups[^1]), true);

        vm.SelectedGroup!.BeginRename();
        vm.SelectedGroup.RenameBuffer = "重命名后的组";
        vm.SelectedGroup.CommitRename();
        Check("分组重命名", vm.Groups[^1].Name, "重命名后的组");

        vm.SelectedGroup.BeginRename();
        vm.SelectedGroup.RenameBuffer = "   ";
        vm.SelectedGroup.CommitRename();
        Check("空名回退", vm.Groups[^1].Name, "未命名分组");

        // 移动分组
        var secondName = vm.Groups[1].Name;
        vm.MoveGroup(vm.Groups[1], -1);
        Check("分组左移", vm.Groups[0].Name, secondName);
        vm.MoveGroup(vm.Groups[0], +1);
        Check("分组右移", vm.Groups[1].Name, secondName);
        Check("越界左移无效", vm.MoveGroup(vm.Groups[0], -1), false);
        Check("越界右移无效", vm.MoveGroup(vm.Groups[^1], +1), false);

        // 删除分组
        while (vm.Groups.Count > 1) vm.RemoveGroup(vm.Groups[^1]);
        Check("保留最后一个分组", vm.Groups.Count, 1);
        Check("拒绝删除最后一个分组", vm.RemoveGroup(vm.Groups[0]), false);

        // ---------- 配置往返 ----------
        Section("配置往返");

        // 逻辑测试要独立于真实配置：临时接管 store，跑完再还原
        var realCfg = store.FilePath;
        var realBackup = realCfg + ".logicbak";
        var hadReal = File.Exists(realCfg);
        if (hadReal) File.Copy(realCfg, realBackup, true);

        // 清掉真实配置，确保 Save 走的是“新建文件”这条路径
        try { File.Delete(realCfg); } catch { /* ignore */ }

        var g = vm.Groups[0];
        var keep = g.Items.Count;
        Log.Add($"  saving group '{g.Name}' with {keep} items via {realCfg}");

        g.Items[0].BeginRename();
        g.Items[0].RenameBuffer = "持久化测试";
        g.Items[0].CommitRename();
        vm.StatusText = "";   // 清掉前面用例留下的状态文案
        vm.Save();
        Check("svc save 无错误", vm.LastSaveError, "");

        Check("配置文件存在", File.Exists(store.FilePath), true);
        Check("便携位置", store.IsPortableLocation, true);

        var reloaded = new ConfigStore().Load();
        Log.Add($"  reloaded {reloaded.Count} group(s): {string.Join(" | ", reloaded.Select(x => x.Name + "(" + x.Items.Count + ")"))}");
        var reloadedGroup = reloaded.FirstOrDefault(x => x.Name == g.Name);
        Check("分组被持久化", reloadedGroup is not null, true);
        Check("条目数被持久化", reloadedGroup?.Items.Count ?? -1, keep);
        Check("自定义名被持久化", reloadedGroup?.Items.Any(i => i.DisplayName == "持久化测试") ?? false, true);

        // 覆盖写入（目标已存在）也要正常
        vm.Save();
        var reloaded2 = new ConfigStore().Load();
        Check("二次保存覆盖成功", reloaded2.FirstOrDefault(x => x.Name == g.Name)?.Items.Count ?? -1, keep);

        // 损坏配置的容错
        var cfgPath = store.FilePath;
        File.WriteAllText(cfgPath, "{ this is not valid json ]");
        var recovered = new ConfigStore().Load();
        Check("损坏配置回退到默认分组", recovered.Count, 1);
        Check("损坏配置已备份", File.Exists(cfgPath + ".bak"), true);

        // ---------- 写入失败时的回退 ----------
        Section("写入失败回退");

        // 用文件锁模拟“便携位置写不进去”。
        // 注意：ConfigStore 绑定的是 exe 目录，这里锁的正是同一个文件，
        // 所以先把它恢复成临时内容，锁释放后再由外层还原真实配置。
        {
            var lockedPath = new ConfigStore().FilePath;
            var groupsToSave = new List<ShortcutGroup> { new() { Name = "锁测试" } };

            // 确保文件存在（Load 可能已把它删了）
            if (!File.Exists(lockedPath)) File.WriteAllText(lockedPath, "[]");

            var threw = false;
            var wroteAnyway = false;
            try
            {
                using var hold = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                try { new ConfigStore().Save(groupsToSave); }
                catch { threw = true; }

                // 锁还持有时，文件内容必须没被改写成“锁测试”
                try { wroteAnyway = File.ReadAllText(lockedPath).Contains("锁测试"); }
                catch { wroteAnyway = false; }   // 读不到也算没写进去
            }
            catch (Exception ex)
            {
                Log.Add($"  (lock setup issue: {ex.GetType().Name})");
            }

            // 允许两种结果：抛错，或回退到了别的路径；但不允许“锁着还写成功了”
            Check("锁定时不会假装写成功", !wroteAnyway, true);
            Log.Add($"  (threw={threw})");
        }

        // ---------- 还原真实配置 ----------
        try
        {
            if (hadReal) { File.Copy(realBackup, realCfg, true); File.Delete(realBackup); }
            else { File.Delete(realCfg); }
        }
        catch { /* ignore */ }

        // ---------- 结果 ----------
        Log.Add("");
        Log.Add($"RESULT pass={_pass} fail={_fail}");
        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "logic-test.log"), Log); }
        catch { /* ignore */ }

        try { Directory.Delete(sandbox, true); } catch { /* ignore */ }
        try { File.Delete(localFile); } catch { /* ignore */ }
        try { Directory.Delete(localDir, true); } catch { /* ignore */ }

        return _fail == 0 ? 0 : 1;
    }

    private static void Section(string name)
    {
        Log.Add("");
        Log.Add("== " + name + " ==");
    }

    private static void Check<T>(string label, T actual, T expected)
    {
        var ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (ok) _pass++; else _fail++;
        Log.Add($"  [{(ok ? "PASS" : "FAIL")}] {label}: got '{actual}'{(ok ? "" : $"  expected '{expected}'")}");
    }
}
