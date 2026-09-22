using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ShortcutManager.Models;
using ShortcutManager.Services;
using ShortcutManager.ViewModels;

namespace ShortcutManager;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private MainWindow? _window;
    private ConfigStore? _store;

    [STAThread]
    public static void Main(string[] args)
    {
        // 自检模式（不启动界面），用于自动化验证图标/配置链路
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        // 逻辑自检模式（不启动界面）：分组/条目/排序/持久化
        if (args.Any(a => string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = LogicTests.Run();
            return;
        }

        // 图标诊断模式（不启动界面）：--icon <路径>，结果写入 icon-diag.log
        var iconIdx = Array.FindIndex(args, a => string.Equals(a, "--icon", StringComparison.OrdinalIgnoreCase));
        if (iconIdx >= 0 && iconIdx + 1 < args.Length)
        {
            var target = args[iconIdx + 1];
            try
            {
                var text = ShellIcons.Diagnose(target, Directory.Exists(target));
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "icon-diag.log"), text, System.Text.Encoding.UTF8);
            }
            catch { /* ignore */ }
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private void OnStartupHandler(object sender, StartupEventArgs e) => StartupCore(e);

    private void StartupCore(StartupEventArgs e)
    {
        // ---- 单实例：重复双击不再开第二个窗口 ----
        // 已有实例时把它的窗口还原出来（可能正被最小化），然后本进程退出。
        _singleInstanceMutex = new Mutex(true, SingleInstanceName, out var isFirst);
        if (!isFirst)
        {
            SignalExistingInstance();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        StartActivationListener();

        // 关掉主窗口 = 退出程序（没有托盘常驻）
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // ---- 载入配置（同步、很快；无需配置文件时几乎零开销）----
        _store = new ConfigStore();
        List<ShortcutGroup> groups;
        try
        {
            groups = _store.Load();
        }
        catch
        {
            groups = new List<ShortcutGroup> { new() { Name = "常用" } };
        }

        var vm = new MainViewModel(_store, groups);

        // 首次运行就把 config.json 落盘，保证便携文件始终存在
        // （即使程序被强制结束，也不会有“配置从来没写过”的窗口期）
        vm.Save();

        _window = new MainWindow(vm);
        MainWindow = _window;

        _window.Show();
        ForceForeground(_window);

        // 启动后预热所有分组的图标缓存（后台线程，不阻塞首屏）
        Task.Run(() =>
        {
            foreach (var g in vm.Groups)
            {
                var items = g.Items.Select(i => i.Model).ToList();
                ImportService.WarmIcons(items);
            }
        });
    }

    /// <summary>
    /// 尽力把窗口切到前台并还原。
    /// Windows 有前台锁（foreground lock），刚启动的进程光调 Activate() 不一定生效，
    /// 这里补一个 AttachThreadInput 的常规做法。
    /// </summary>
    private static void ForceForeground(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var foreground = GetForegroundWindow();
            if (foreground == hwnd) return;

            var targetThread = GetWindowThreadProcessId(foreground, out _);
            var ourThread = GetCurrentThreadId();
            if (targetThread != 0 && targetThread != ourThread)
            {
                AttachThreadInput(targetThread, ourThread, true);
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(targetThread, ourThread, false);
                }
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch { /* 抢不到焦点也不影响使用 */ }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>把窗口还原到前台（重复启动 exe、或从最小化恢复时用）。</summary>
    public void BringToFront()
    {
        if (_window is null) return;
        _window.Show();
        ForceForeground(_window);
    }

    // ==================================================================
    //  单实例之间的“唤出窗口”通知（命名事件，无需窗口消息）
    // ==================================================================

    private const string SingleInstanceName = @"Local\ShortcutManager.SingleInstance";
    private const string ActivateEventName = @"Local\ShortcutManager.Activate";
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateRegistration;

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch { /* ignore */ }
    }

    private void StartActivationListener()
    {
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            // 回调在线程池上跑，切回 UI 线程再动窗口
            _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activateSignal,
                (_, _) => Dispatcher.BeginInvoke(new Action(BringToFront)),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _window?.ViewModel.Save(); } catch { /* ignore */ }
        try { _activateRegistration?.Unregister(null); } catch { /* ignore */ }
        try { _activateSignal?.Dispose(); } catch { /* ignore */ }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        try { _singleInstanceMutex?.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
