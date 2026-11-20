using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShortcutManager.Services;

namespace ShortcutManager;

/// <summary>
/// 自动化验证与诊断协调器（--diag / --uitest / --shot / --traytest）。
/// 从 MainWindow 拆离，保持主窗口生产逻辑简洁。
/// </summary>
internal static class AutomationRunner
{
    internal static void TryRun(MainWindow win)
    {
        var args = Environment.GetCommandLineArgs();

        if (args.Any(a => a == "--diag"))
            RunDiag(win);

        if (args.Any(a => a == "--uitest"))
            RunUiTest(win);

        var shotIndex = Array.FindIndex(args, a => a == "--shot");
        if (shotIndex >= 0 && shotIndex + 1 < args.Length)
            RunShot(win, args[shotIndex + 1]);

        if (args.Any(a => a == "--traytest"))
            RunTrayTest(win);
    }

    private static void RunDiag(MainWindow win)
    {
        var src = PresentationSource.FromVisual(win);
        var m = src?.CompositionTarget?.TransformToDevice;
        var lines = new[]
        {
            $"Width(DIP)={win.Width} Height(DIP)={win.Height}",
            $"ActualWidth(DIP)={win.ActualWidth} ActualHeight(DIP)={win.ActualHeight}",
            $"TransformToDevice={m}",
            $"TileList.ActualWidth={win.TileList.ActualWidth} TileList.ActualHeight={win.TileList.ActualHeight}",
            $"TabList.ActualWidth={win.TabList.ActualWidth}",
            $"TabScroll.ScrollableWidth={win.TabScroll.ScrollableWidth:0.0} TabArrows={win.TabArrows.Visibility}",
            $"maximizeBox={WindowEffects.HasMaximizeBox(win)}",
            $"Groups={win.ViewModel.Groups.Count} Items={win.ViewModel.SelectedItems?.Count}",
        };
        TryWriteLog("diag.log", lines);
    }

    private static void RunUiTest(MainWindow win)
    {
        var log = new List<string>();
        void UiLog(string line)
        {
            log.Add(line);
            TryWriteLog("ui-test.log", log.ToArray());
        }

        win.ViewModel.SuppressSave = true;
        win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(700);
            var ok = true;
            static string Pct(double v) => v.ToString("0.0");

            var noBox = !WindowEffects.HasMaximizeBox(win);
            UiLog($"expect-no-maximizebox(WS_MAXIMIZEBOX 已移除)={noBox}");
            ok &= noBox;

            win.WindowState = WindowState.Maximized;
            await Task.Delay(300);
            var rejected = win.WindowState != WindowState.Maximized;
            UiLog($"expect-maximize-rejected(设 Maximized 后被拉回)={rejected} state={win.WindowState}");
            ok &= rejected;
            if (win.WindowState != WindowState.Normal) win.WindowState = WindowState.Normal;
            await Task.Delay(250);

            while (win.ViewModel.Groups.Count > 2) win.ViewModel.RemoveGroup(win.ViewModel.Groups[^1]);
            await Task.Delay(300);
            win.UpdateTabArrows();
            var few = win.TabArrows.Visibility == Visibility.Collapsed && win.TabScroll.ScrollableWidth <= 0.5;
            UiLog($"few-groups: groups={win.ViewModel.Groups.Count} scrollable={Pct(win.TabScroll.ScrollableWidth)} " +
                  $"arrows={win.TabArrows.Visibility} => 不显示={few}");
            ok &= few;

            while (win.ViewModel.Groups.Count < 14) win.ViewModel.AddGroup();
            win.ViewModel.SelectedGroup = win.ViewModel.Groups[0];
            await Task.Delay(500);
            win.ScrollTabs(-9999);
            win.UpdateTabArrows();
            var many = win.TabArrows.Visibility == Visibility.Visible
                       && win.TabScroll.ScrollableWidth > 0.5
                       && !win.TabLeftButton.IsEnabled && win.TabRightButton.IsEnabled;
            UiLog($"many-groups: groups={win.ViewModel.Groups.Count} scrollable={Pct(win.TabScroll.ScrollableWidth)} " +
                  $"offset={Pct(win.TabScroll.HorizontalOffset)} arrows={win.TabArrows.Visibility} " +
                  $"left={win.TabLeftButton.IsEnabled} right={win.TabRightButton.IsEnabled} => 显示且方向可用={many}");
            ok &= many;

            win.TabRightButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(250);
            var scrolled = win.TabScroll.HorizontalOffset > 0.5;
            UiLog($"expect-arrow-scrolls(点右箭头后偏移变大)={scrolled} offset={Pct(win.TabScroll.HorizontalOffset)}");
            ok &= scrolled;

            win.ScrollTabs(9999);
            await Task.Delay(250);
            win.UpdateTabArrows();
            var atEnd = win.TabScroll.HorizontalOffset >= win.TabScroll.ScrollableWidth - 0.5
                        && !win.TabRightButton.IsEnabled && win.TabLeftButton.IsEnabled;
            UiLog($"expect-arrow-bounds(到底后右禁用/左可用)={atEnd} offset={Pct(win.TabScroll.HorizontalOffset)}");
            ok &= atEnd;

            var tmp = Path.Combine(Path.GetTempPath(), "sm-uitest");
            Directory.CreateDirectory(tmp);
            var files = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var f = Path.Combine(tmp, $"t{i}.txt");
                File.WriteAllText(f, "x");
                files.Add(f);
            }
            win.ViewModel.Import(files);
            await Task.Delay(250);

            var group = win.ViewModel.SelectedGroup!;
            var before = group.Items.Count;
            win.TileList.SelectedItems.Clear();
            foreach (var it in group.Items.Skip(Math.Max(0, before - 3))) win.TileList.SelectedItems.Add(it);
            var selected = win.SelectedTiles().Count;
            win.RemoveSelectedItems();
            await Task.Delay(250);
            var removed = before - group.Items.Count;

            var multi = win.TileList.SelectionMode == SelectionMode.Extended && selected == 3 && removed == 3;
            UiLog($"expect-multi-delete(mode={win.TileList.SelectionMode} 选中{selected} 移除{removed})={multi}");
            ok &= multi;
            UiLog($"keep-files(磁盘文件未被删除)={files.All(File.Exists)}");
            ok &= files.All(File.Exists);

            try { Directory.Delete(tmp, true); } catch { /* ignore */ }

            win.ApplyPinned(true, save: false);
            var pinned = win.Topmost && win.Settings.AlwaysOnTop && win.PinToggle.IsChecked == true;
            win.ApplyPinned(false, save: false);
            var unpinned = !win.Topmost && !win.Settings.AlwaysOnTop && win.PinToggle.IsChecked == false;
            UiLog($"expect-pin(置顶开/关都生效)={pinned && unpinned} pinned={pinned} unpinned={unpinned}");
            ok &= pinned && unpinned;

            win.Settings.KeepFrontAfterOpen = true;
            win.KeepFrontBriefly();
            var bumped = win.Topmost;
            await Task.Delay(MainWindow.KeepFrontMilliseconds + 600);
            var reverted = !win.Topmost;
            UiLog($"expect-keep-front(打开后短暂置顶，{MainWindow.KeepFrontMilliseconds}ms 后自动还原)=" +
                  $"{bumped && reverted} bumped={bumped} reverted={reverted}");
            ok &= bumped && reverted;

            UiLog($"result={(ok ? "PASS" : "FAIL")}");
            Environment.ExitCode = ok ? 0 : 1;
            win.ExitApplicationForTest();
        }));
    }

    private static void RunShot(MainWindow win, string outPath)
    {
        win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
            {
                await Task.Delay(900);
                CaptureToFile(win, outPath);
                Application.Current.Shutdown();
            }));
        }));
    }

    private static void RunTrayTest(MainWindow win)
    {
        var log = new List<string>();
        void TrayLog(string line)
        {
            log.Add(line);
            TryWriteLog("tray-test.log", log.ToArray());
        }

        win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(700);

            var ok = true;
            TrayLog($"start: visible={win.IsVisible} inTaskbar={win.ShowInTaskbar} state={win.WindowState}");
            ok &= win.IsVisible && win.ShowInTaskbar && win.WindowState == WindowState.Normal;

            win.WindowState = WindowState.Minimized;
            await Task.Delay(500);
            var normalMin = win.WindowState == WindowState.Minimized && win.ShowInTaskbar && !win.IsTrayIconVisible;
            TrayLog($"expect-normal-minimize(最小化但仍在任务栏、无托盘)={normalMin}");
            ok &= normalMin;

            win.WindowState = WindowState.Normal;
            await Task.Delay(300);

            win.Close();
            await Task.Delay(500);
            var toTray = !win.IsVisible && !win.ShowInTaskbar && win.IsTrayIconVisible;
            TrayLog($"expect-close-to-tray(窗口隐藏+无任务栏按钮+托盘图标存在)={toTray}");
            ok &= toTray;

            win.SimulateTrayClick(System.Windows.Forms.MouseButtons.Right);
            await Task.Delay(250);
            var rightNoop = !win.IsVisible && !win.ShowInTaskbar;
            TrayLog($"expect-rightclick-noop(右键不恢复窗口)={rightNoop}");
            ok &= rightNoop;

            TrayLog("wait-for-focus-steal(3000ms)：外部脚本可在此时激活别的窗口");
            await Task.Delay(3000);
            TrayLog($"before-restore: 前台进程={App.ForegroundOwnerName()}");

            win.SimulateTrayClick(System.Windows.Forms.MouseButtons.Left);
            await Task.Delay(500);
            var back = win.IsVisible && win.ShowInTaskbar && win.WindowState == WindowState.Normal;
            TrayLog($"expect-restored(窗口可见+任务栏按钮+Normal)={back}");
            ok &= back;

            var notIconic = !App.IsMinimizedHwnd(win);
            TrayLog($"expect-not-minimized(Win32 IsIconic=false)={notIconic}");
            ok &= notIconic;

            var isForeground = App.IsForeground(win);
            TrayLog($"expect-foreground(Win32 前台=本窗口)={isForeground}");
            ok &= isForeground;

            TrayLog($"result={(ok ? "PASS" : "FAIL")}");
            Environment.ExitCode = ok ? 0 : 1;
            win.ExitApplicationForTest();
        }));
    }

    private static void CaptureToFile(MainWindow win, string path)
    {
        try
        {
            var w = (int)Math.Ceiling(win.ActualWidth);
            var h = (int)Math.Ceiling(win.ActualHeight);
            if (w <= 0 || h <= 0) { TryWriteLog("shot.log", new[] { $"bad size {w}x{h}" }); return; }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);

            var src = PresentationSource.FromVisual(win);
            var lines = new List<string>
            {
                $"saved={path}",
                $"size(DIP)={w}x{h}",
                $"transform={src?.CompositionTarget?.TransformToDevice}",
                $"groups={win.ViewModel.Groups.Count} items={win.ViewModel.SelectedItems?.Count}",
            };

            if (win.ViewModel.SelectedItems is { } items)
            {
                foreach (var it in items)
                {
                    var img = it.Icon;
                    lines.Add($"icon '{it.Name}' path='{it.Path}' stored='{it.StoredPath}' exists={it.Exists} " +
                              $"image={(img is null ? "null" : $"{img.GetType().Name} {img.Width}x{img.Height}")} " +
                              $"builtInFallback={ShellIcons.IsBuiltInFallback(img, it.IsDirectory)}");
                }
            }

            TryWriteLog("shot.log", lines.ToArray());
        }
        catch (Exception ex)
        {
            TryWriteLog("shot.log", new[] { "FAIL " + ex });
        }
    }

    private static void TryWriteLog(string name, string[] lines)
    {
        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, name), lines); }
        catch { /* ignore */ }
    }
}
