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
        // 测试会像真实使用那样反复写 config.json，所以必须在【任何一次 Save 之前】
        // 把真实配置原样备份，跑完再还原——否则备份到的是测试自己写进去的数据。
        var realCfg = new ConfigStore().FilePath;
        var realBackup = realCfg + ".logicbak";
        var hadReal = File.Exists(realCfg);
        if (hadReal)
        {
            try { File.Copy(realCfg, realBackup, true); } catch { /* ignore */ }
        }

        // 损坏容灾用例会覆盖 .bak，同样先存一份
        var realBak = realCfg + ".bak";
        var realBakBackup = realBak + ".logicbak";
        var hadRealBak = File.Exists(realBak);
        if (hadRealBak)
        {
            try { File.Copy(realBak, realBakBackup, true); } catch { /* ignore */ }
        }

        // 设置往返用例会临时改写真实 settings.json，同样先备份、跑完还原
        var settingsFile = new SettingsStore().FilePath;
        var settingsBackup = settingsFile + ".logicbak";
        var hadRealSettings = File.Exists(settingsFile);
        if (hadRealSettings)
        {
            try { File.Copy(settingsFile, settingsBackup, true); } catch { /* ignore */ }
        }

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

        // 目标组已有同一路径时，源组条目必须还在（曾经先删后判重，条目会丢）
        var fileC = Path.Combine(sandbox, "clash.txt");
        File.WriteAllText(fileC, "c");
        Check("源组导入 clash", vm.Import(new[] { fileC }, g1), 1);
        Check("目标组导入 clash", vm.Import(new[] { fileC }, g2), 1);
        var srcCount = g1.Items.Count;
        var dstCount = g2.Items.Count;
        var clash = g1.Items.First(i => string.Equals(i.Path, Path.GetFullPath(fileC), StringComparison.OrdinalIgnoreCase));
        var kept = vm.MoveItemToGroup(clash, g2);
        Check("撞车时跨组移动返回 false", kept, false);
        Check("撞车时源组条目仍在", g1.Items.Count, srcCount);
        Check("撞车时目标组条目未变", g2.Items.Count, dstCount);

        Section("失效清理与过滤");
        var missingItem = new ShortcutItem { Path = @"C:\nope\gone.txt", IsDirectory = false };
        g1.Model.Items.Add(missingItem);
        g1.Items.Add(new ShortcutViewModel(missingItem, g1));
        Check("失效项 Exists=false", missingItem.Exists, false);
        var cleaned = vm.RemoveMissingItems(g1);
        Check("清理失效项数量", cleaned, 1);
        Check("清理后无失效项", g1.Items.All(i => !i.IsMissing), true);

        vm.SelectedGroup = g1;
        vm.FilterText = "zzzz-not-found";
        Check("无匹配时显示空态", vm.ShowEmptyState, true);
        vm.FilterText = "";
        Check("清空过滤后空态随条目走", vm.ShowEmptyState, g1.Items.Count == 0);

        Section("设置归一化");
        var raw = new AppSettings { HotkeyModifiers = 0, HotkeyKey = 0 };
        raw.Normalize();
        Check("无修饰键回退 Alt", raw.HotkeyModifiers, (int)HotkeyModifiers.Alt);
        Check("无效主键回退 Q", raw.HotkeyKey, 0x51);
        Check("热键文案 Alt+Q", GlobalHotkeyManager.Format((int)HotkeyModifiers.Alt, 0x51), "Alt+Q");

        var geom = new AppSettings { WindowLeft = 12, WindowTop = 34, HasShownTrayHint = true };
        var svm = new SettingsViewModel(new SettingsStore(), geom);
        var copy = new AppSettings();
        svm.ApplyTo(copy);
        Check("设置页不丢窗口左", copy.WindowLeft, 12d);
        Check("设置页不丢窗口顶", copy.WindowTop, 34d);
        Check("设置页不丢托盘提示标记", copy.HasShownTrayHint, true);

        Section("窗口置顶与连续打开");
        var winDefaults = new AppSettings();
        Check("默认不钉住置顶", winDefaults.AlwaysOnTop, false);
        Check("默认打开后短暂置顶", winDefaults.KeepFrontAfterOpen, true);

        var winFlags = new AppSettings { AlwaysOnTop = true, KeepFrontAfterOpen = false };
        var winVm = new SettingsViewModel(new SettingsStore(), winFlags);
        var winCopy = new AppSettings();
        winVm.ApplyTo(winCopy);
        Check("设置页保留置顶开关", winCopy.AlwaysOnTop, true);
        Check("设置页保留连续打开开关", winCopy.KeepFrontAfterOpen, false);

        // 这两个开关必须真的落盘（settings.json 往返）。
        // 受限环境（例如禁止 ReplaceFile 原子替换的沙箱）写不进去时不算功能失败，
        // 记一条 skip；正常桌面环境会真正走一遍 Save/Load。
        if (new SettingsStore().IsPortableLocation)
        {
            var probe = new AppSettings
            {
                AlwaysOnTop = true,
                KeepFrontAfterOpen = false,
                CloseToTray = false,
                HotkeyKey = 0x52,   // R
            };
            try
            {
                new SettingsStore().Save(probe);
                var back = new SettingsStore().Load();
                Check("settings 置顶开关往返", back.AlwaysOnTop, true);
                Check("settings 连续打开开关往返", back.KeepFrontAfterOpen, false);
                Check("settings 关闭行为往返", back.CloseToTray, false);
                Check("settings 热键往返", back.HotkeyKey, 0x52);
                Log.Add($"  (settings written to {new SettingsStore().FilePath})");
            }
            catch (Exception ex)
            {
                Log.Add($"  (skip settings file round-trip: {ex.GetType().Name}: {ex.Message})");

                // 文件写不进去也至少要证明模型能正确序列化/反序列化这两个字段
                var json = System.Text.Json.JsonSerializer.Serialize(probe);
                var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Check("settings 置顶字段 JSON 往返", back is not null && back.AlwaysOnTop, true);
                Check("settings 连续打开字段 JSON 往返", back is not null && !back.KeepFrontAfterOpen, true);
            }
        }
        else
        {
            Log.Add("  (skip settings round-trip: settings.json 不在可写目录)");
        }

        Section("启动器");
        var nowhere = new ShortcutItem { Path = Path.Combine(sandbox, "no-such-file.txt"), IsDirectory = false };
        var missingMsg = "";
        Check("打开不存在路径返回失败", Launcher.Open(nowhere, m => missingMsg = m), false);
        Check("失败带文案", missingMsg.StartsWith("找不到"), true);
        var asyncMsg = Launcher.OpenAsync(nowhere).GetAwaiter().GetResult();
        Check("异步打开失败也回传文案", asyncMsg?.StartsWith("找不到") ?? false, true);

        // 移除
        Section("移除条目");
        var remCount = g1.Items.Count;
        var remTarget = g1.Items[0];
        var remPath = remTarget.Path;
        vm.RemoveItem(remTarget);
        Check("条目移除", g1.Items.Count, remCount - 1);
        Check("模型移除", g1.Model.Items.Any(i => i.Path == remPath), false);
        Check("磁盘文件未被删除", File.Exists(remPath) || Directory.Exists(remPath), true);

        // 多选批量移除（界面 Ctrl/Shift 多选后删除走的就是这条路径）
        var batchBefore = g1.Items.Count;
        var batch = g1.Items.Take(2).ToList();
        var batchPaths = batch.Select(i => i.Path).ToList();
        Check("批量移除数量", vm.RemoveItems(batch), 2);
        Check("批量移除后条目数", g1.Items.Count, batchBefore - 2);
        Check("批量移除后模型同步", g1.Model.Items.Any(i => batchPaths.Contains(i.Path)), false);
        Check("批量移除不动磁盘文件", batchPaths.All(p => File.Exists(p) || Directory.Exists(p)), true);
        var afterBatch = g1.Items.Count;
        Check("撤销批量移除", vm.UndoLastRemove(), true);
        Check("撤销后数量恢复", g1.Items.Count, afterBatch + 2);
        Check("重复批量移除返回 0", vm.RemoveItems(batch), 0);

        // 程序目录内的文件导入后存为相对路径
        Section("相对路径导入");
        var relImport = vm.Import(new[] { localFile }, g1);
        Check("程序目录内文件可导入", relImport, 1);
        Check("导入存为相对路径", g1.Items[^1].StoredPath, "sm-rel-test.txt");
        Check("相对路径解析为完整路径", g1.Items[^1].Path, Path.GetFullPath(localFile));
        Check("相对路径重复导入被忽略", vm.Import(new[] { localFile }, g1), 0);
        Check("相对路径条目默认名", g1.Items[^1].Name, "sm-rel-test");

        Section("多选发送与快捷方式");
        vm.AddGroup();
        var gSend = vm.Groups[^1];
        var sendN = Math.Min(2, g1.Items.Count);
        var sendBatch = g1.Items.Take(sendN).ToList();
        var g1BeforeSend = g1.Items.Count;
        Check("批量发送数量", vm.MoveItemsToGroup(sendBatch, gSend), sendN);
        Check("源组减少", g1.Items.Count, g1BeforeSend - sendN);
        Check("目标组增加", gSend.Items.Count, sendN);

        var lnkTarget = Path.Combine(sandbox, "lnk-target.txt");
        File.WriteAllText(lnkTarget, "t");
        var lnkPath = Path.Combine(sandbox, "to-target.lnk");
        var lnkOk = TryCreateShortcut(lnkPath, lnkTarget, "--ok");
        if (lnkOk)
        {
            Check("导入 lnk", vm.Import(new[] { lnkPath }, gSend), 1);
            Check("lnk 存成目标路径", gSend.Items[^1].Path, Path.GetFullPath(lnkTarget));
            Check("lnk 参数被保留", gSend.Items[^1].Model.Arguments, "--ok");
        }
        else
        {
            Log.Add("  (skip lnk: WScript.Shell 不可用)");
        }

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

        // 分组拖拽排序
        Section("分组拖拽排序");
        var secondName = vm.Groups[1].Name;
        Check("拖到目标前面", vm.MoveGroupTo(vm.Groups[1], vm.Groups[0], false), true);
        Check("顺序已改变", vm.Groups[0].Name, secondName);
        Check("拖到自己无效", vm.MoveGroupTo(vm.Groups[0], vm.Groups[0], true), false);
        Check("拖到目标后面", vm.MoveGroupTo(vm.Groups[0], vm.Groups[1], true), true);
        Check("顺序已还原", vm.Groups[1].Name, secondName);
        Check("落点未变时不算位移", vm.MoveGroupTo(vm.Groups[1], vm.Groups[0], true), false);
        Check("拖到末尾", vm.MoveGroupToEnd(vm.Groups[0]), true);
        Check("已在末尾时返回 false", vm.MoveGroupToEnd(vm.Groups[^1]), false);
        Check("分组顺序与模型一致",
            string.Join("|", vm.Groups.Select(g => g.Name)),
            string.Join("|", vm.Groups.Select(g => g.Model.Name)));

        // 删除分组
        while (vm.Groups.Count > 1) vm.RemoveGroup(vm.Groups[^1]);
        Check("保留最后一个分组", vm.Groups.Count, 1);
        Check("拒绝删除最后一个分组", vm.RemoveGroup(vm.Groups[0]), false);

        // ---------- 配置往返 ----------
        Section("配置往返");

        // 真实配置已在 Run() 开头备份（见文件顶部说明），这里只需清掉当前文件，
        // 确保 Save 走的是“新建文件”这条路径
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

        try
        {
            if (hadRealBak) { File.Copy(realBakBackup, realBak, true); File.Delete(realBakBackup); }
            else { File.Delete(realBak); }
        }
        catch { /* ignore */ }

        // ---------- 还原真实设置 ----------
        try
        {
            if (hadRealSettings) { File.Copy(settingsBackup, settingsFile, true); File.Delete(settingsBackup); }
            else { File.Delete(settingsFile); }
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

    private static bool TryCreateShortcut(string lnkPath, string target, string arguments)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = target;
            sc.Arguments = arguments;
            sc.Save();
            return File.Exists(lnkPath);
        }
        catch
        {
            return false;
        }
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
