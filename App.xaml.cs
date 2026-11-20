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
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private GlobalHotkeyManager? _hotkeys;
    private SettingsWindow? _settingsWindow;

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
        if (IsAutomationRun)
        {
            StartApp();
            return;
        }

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
        StartApp();
    }

    /// <summary>只给自动化/调试用的开关，启动时不做单实例拦阻。</summary>
    internal static readonly string[] AutomationSwitches =
    {
        "--traytest", "--uitest", "--shot", "--import", "--diag", "--hotkeytest",
    };

    /// <summary>自动化入口：测试里不要写窗口位置、也不要弹托盘气泡。</summary>
    internal static bool IsAutomationRun
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            return args.Any(a => AutomationSwitches.Contains(a.ToLowerInvariant()));
        }
    }

    private void StartApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- 载入设置（settings.json，与 config.json 分离）----
        _settingsStore = new SettingsStore();
        try { _settings = _settingsStore.Load(); }
        catch { _settings = new AppSettings(); }

        // ---- 载入配置 ----
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

        // UI 启动延迟磁盘探测：避免网络盘/慢速介质阻塞首屏显示
        var vm = new MainViewModel(_store, groups, deferDiskProbe: true);

        // 仅在首次运行（配置文件尚不存在）时落盘，避免每次启动无谓重写磁盘
        if (!File.Exists(_store.FilePath))
            vm.Save();

        _window = new MainWindow(vm, _settings, _settingsStore);
        MainWindow = _window;

        _window.ExitRequested += () => Shutdown();

        SyncAutoStart();

        if (_settings.StartMinimized)
        {
            _window.HideToTrayOnStartup();
        }
        else
        {
            _window.Show();
            ForceForeground(_window);
        }

        // 全局唤起快捷键：注册到主窗口消息循环（窗口在托盘/隐藏时也生效）
        _hotkeys = new GlobalHotkeyManager();
        _hotkeys.Activated += OnHotkeyActivated;
        _hotkeys.Attach(_window, _settings);

        if (!IsAutomationRun && !_hotkeys.IsRegistered)
        {
            var name = GlobalHotkeyManager.Format(_settings.HotkeyModifiers, _settings.HotkeyKey);
            vm.StatusText = $"全局热键 {name} 被占用，可在设置里更换";
        }

        // ---- 全局快捷键验证（--hotkeytest）----
        if (Environment.GetCommandLineArgs().Any(a => a == "--hotkeytest"))
        {
            var log = new List<string>();
            log.Add($"registered={_hotkeys!.IsRegistered}");
            log.Add($"hwnd={App.HandleOf(_window)}");
            log.Add($"modifiers={_settings.HotkeyModifiers} key={_settings.HotkeyKey} (default Alt+Q = 1/81)");
            _window.HideToTrayOnStartup();
            _hotkeys.SimulateHotkey();
            System.Threading.Thread.Sleep(500);
            log.Add($"after-simulate visible={_window.IsVisible} inTaskbar={_window.ShowInTaskbar} state={_window.WindowState}");
            var ok = _hotkeys.IsRegistered && _window.IsVisible;
            log.Add($"result={(ok ? "PASS" : "FAIL")}");
            Environment.ExitCode = ok ? 0 : 1;
            try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "hotkey-test.log"), log, System.Text.Encoding.UTF8); } catch { }
            _window.ExitApplicationForTest();
        }
    }

    /// <summary>全局快捷键触发：唤起主窗口（若在托盘则恢复，否则置前）。</summary>
    private void OnHotkeyActivated()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.RestoreFromTray();
        else App.ForceForeground(_window);
    }

    private void SyncAutoStart()
    {
        try { AutoStartService.SetEnabled(_settings.AutoStart); }
        catch { /* ignore */ }
    }

    /// <summary>打开设置窗口（托盘菜单/底部按钮“设置”调用）。</summary>
    internal void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var vm = new SettingsViewModel(_settingsStore!, _settings);
        _settingsWindow = new SettingsWindow(vm, _hotkeys!) { Owner = _window };
        _settingsWindow.Closed += (_, _) =>
        {
            // SettingsViewModel 改动已实时落盘，关闭时仅同步内存与运行时状态
            _settings = _settingsWindow.AppliedSettings;
            _hotkeys?.Reregister(_settings);
            SyncAutoStart();
            _window?.ApplyWindowSettings();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    internal static void ForceForeground(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            window.Activate();
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

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    internal static IntPtr HandleOf(Window window) => new WindowInteropHelper(window).Handle;

    internal static bool IsMinimizedHwnd(Window window)
    {
        var hwnd = HandleOf(window);
        return hwnd != IntPtr.Zero && IsIconic(hwnd);
    }

    internal static bool IsForeground(Window window)
    {
        var hwnd = HandleOf(window);
        return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
    }

    internal static string ForegroundOwnerName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "(none)";
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "(unknown)";
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return "(error)"; }
    }

    public void BringToFront() => _window?.RestoreFromTray();

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
        try { _window?.CleanupOnExit(); } catch { /* ignore */ }
        try { _hotkeys?.Dispose(); } catch { /* ignore */ }
        try { _settingsWindow?.Close(); } catch { /* ignore */ }
        try { _activateRegistration?.Unregister(null); } catch { /* ignore */ }
        try { _activateSignal?.Dispose(); } catch { /* ignore */ }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        try { _singleInstanceMutex?.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
