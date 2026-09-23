// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.IO;
using System.Windows;
using System.Windows.Threading;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 应用入口：无主窗口，组装 托盘图标 / 托盘菜单 / 置顶管理 / 选择模式 四个服务。
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private PinManager? _pinManager;
    private TrayIconService? _tray;
    private TrayContextMenu? _menu;
    private SelectionService? _selection;
    private AppSettings _settings = new();
    private HotkeyService? _hotkeys;
    private SettingsWindow? _settingsWindow;
    private PinnedPanelWindow? _panelWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常兜底：还原系统光标 → 提示 → 退出（不留下被替换的光标）
        DispatcherUnhandledException += (_, args) =>
        {
            SelectionService.RestoreSystemCursors();
            MessageBox.Show($"MakingTop 发生异常，即将退出：\n{args.Exception.Message}",
                "MakingTop", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
            Shutdown(1);
        };

        // 单实例：二次启动直接提示并退出，避免出现两个托盘图标
        _singleInstanceMutex = new Mutex(true, @"Local\MakingTop.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("MakingTop 已在运行（请查看任务栏托盘图标）。",
                "MakingTop", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 服务组装
        _pinManager = new PinManager();
        _tray = new TrayIconService();
        _selection = new SelectionService(_pinManager);
        _menu = new TrayContextMenu();

        // 设置与全局热键：加载持久化配置并注册，热键分发到对应动作
        _settings = AppSettings.Load();
        _hotkeys = new HotkeyService(_tray);
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        ApplyStartupHotkeys();

        // 托盘点击 → 弹菜单（左键/右键都弹，交互更顺手）
        _tray.TrayRightClick += ShowTrayMenu;
        _tray.TrayLeftClick += ShowTrayMenu;

        // 菜单命令
        _menu.StartSelectionRequested += () => _selection.Toggle();
        _menu.UnpinLastRequested += () => _pinManager.UnpinLast();
        _menu.UnpinWindowRequested += hwnd => _pinManager.UnpinWindow(hwnd);
        _menu.UnpinAllRequested += () => _pinManager.UnpinAll();
        _menu.SettingsRequested += OpenSettings;
        _menu.ExitRequested += ExitApplication;

        // 状态变化 → 刷新菜单文案（禁用态/窗口标题）
        _pinManager.PinsChanged += UpdateMenuState;
        _selection.StateChanged += UpdateMenuState;

        UpdateMenuState();
        _tray.Show();

        // 隐藏的开发者自检入口：MakingTop.exe --selftest
        // 自动走一遍真实置顶链路（PinWindow → 悬浮图标创建 → WinEvent 事件流 → 取消置顶）
        if (e.Args.Contains("--selftest"))
            RunSelfTest();

        // 隐藏的 UI 冒烟入口：MakingTop.exe --smoke-ui
        // 创建设置窗口与置顶管理面板 2 秒后自动关闭（验证窗口可无异常构建）
        if (e.Args.Contains("--smoke-ui"))
        {
            OpenSettings();
            ShowPanel();
            var smoke = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            smoke.Tick += (_, _) =>
            {
                smoke.Stop();
                _settingsWindow?.Close();
                _panelWindow?.CloseWithFade();
                ExitApplication();
            };
            smoke.Start();
        }
    }

    /// <summary>启动时按持久化配置注册全部热键（失败静默，设置窗口里可见状态）。</summary>
    private void ApplyStartupHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.Apply(HotkeyService.IdSelect, _settings.HotkeySelect);
        _hotkeys.Apply(HotkeyService.IdUnpinCurrent, _settings.HotkeyUnpinCurrent);
        _hotkeys.Apply(HotkeyService.IdUnpinAll, _settings.HotkeyUnpinAll);
        _hotkeys.Apply(HotkeyService.IdShowPanel, _settings.HotkeyShowPanel);
    }

    /// <summary>全局热键 → 对应动作。</summary>
    private void OnHotkeyPressed(int id)
    {
        if (_selection == null || _pinManager == null) return;
        switch (id)
        {
            case HotkeyService.IdSelect:
                _selection.Toggle();
                break;
            case HotkeyService.IdUnpinCurrent:
                _pinManager.UnpinLast();
                break;
            case HotkeyService.IdUnpinAll:
                _pinManager.UnpinAll();
                break;
            case HotkeyService.IdShowPanel:
                TogglePanel();
                break;
        }
    }

    /// <summary>打开设置窗口（已打开则激活，单例）。</summary>
    private void OpenSettings()
    {
        if (_hotkeys == null) return;
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _hotkeys, () => ShowPanel());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>打开置顶管理面板（已打开则激活）。</summary>
    private void ShowPanel()
    {
        if (_pinManager == null) return;
        if (_panelWindow != null)
        {
            _panelWindow.Activate();
            return;
        }
        _panelWindow = new PinnedPanelWindow(_pinManager);
        _panelWindow.Closed += (_, _) => _panelWindow = null;
        _panelWindow.Show();
        _panelWindow.Activate();
    }

    /// <summary>热键切换面板：开 → 关（关 → 开）。</summary>
    private void TogglePanel()
    {
        if (_panelWindow != null) _panelWindow.CloseWithFade();
        else ShowPanel();
    }

    /// <summary>
    /// 自动化自检：置顶一个外部可见窗口，3 秒后校验悬浮图标真实存在，再恢复原状。
    /// 结果写入 %TEMP%\MakingTop-selftest.log，进程退出码 0=通过 2=失败。
    /// </summary>
    private void RunSelfTest()
    {
        string logPath = Path.Combine(Path.GetTempPath(), "MakingTop-selftest.log");
        var log = new System.Text.StringBuilder();

        IntPtr target = FindSelfTestTarget();
        log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] target=0x{target.ToInt64():X} " +
                       $"'{NativeMethods.GetWindowText(target)}' valid={SelectionService.IsValidTarget(target)}");

        if (target == IntPtr.Zero || !SelectionService.IsValidTarget(target))
        {
            log.AppendLine("RESULT=FAIL (no valid target window)");
            AppendSelfTestLog(logPath, log.ToString());
            Shutdown(2);
            return;
        }

        _pinManager!.PinWindow(target);

        IntPtr overlayHwndRef = IntPtr.Zero;

        // 第 3 秒：基础校验 + 模拟用户点击激活目标窗口（触发 Z 序洗牌，复现覆盖问题场景）
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                IntPtr overlayHwnd = _pinManager!.GetOverlayHwndFor(target);
                bool overlayAlive = overlayHwnd != IntPtr.Zero;
                bool targetTopmost = overlayAlive &&
                    (GetWindowLongW(target, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
                overlayHwndRef = overlayHwnd;

                log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] pinned={_pinManager.AnyPinned} " +
                               $"overlayAlive={overlayAlive} targetTopmost={targetTopmost}");

                SetForegroundWindow(target); // 模拟激活（等价于点击该窗口）

                // 激活后 1.5 秒做第二段校验（此处才启动，保证 overlayHwndRef 已就绪）
                var timer2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer2.Tick += (_, _) =>
                {
                    timer2.Stop();
                    try
                    {
                        bool alive2 = overlayHwndRef != IntPtr.Zero &&
                            _pinManager!.GetOverlayHwndFor(target) != IntPtr.Zero;
                        bool zOrderOk = alive2 && GetWindow(target, GW_HWNDPREV) == overlayHwndRef;

                        log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 激活后 overlayAlive={alive2} " +
                                       $"overlayAboveTarget={zOrderOk}");
                        log.AppendLine(alive2 && zOrderOk ? "RESULT=PASS" : "RESULT=FAIL");
                    }
                    finally
                    {
                        _pinManager!.UnpinAll();
                        AppendSelfTestLog(logPath, log.ToString());
                        ExitApplication();
                    }
                };
                timer2.Start();
            }
            catch (Exception ex)
            {
                log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] 第一段校验异常: {ex.Message}");
                AppendSelfTestLog(logPath, log.ToString());
                ExitApplication();
            }
        };
        timer.Start();
    }

    /// <summary>自检日志安全写入：提权进程不向用户可控的符号链接/junction 目标追加（防受控文件写入）。</summary>
    private static void AppendSelfTestLog(string logPath, string content)
    {
        try
        {
            var fi = new FileInfo(logPath);
            if (fi.Exists && fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return; // 既有文件是链接 → 拒绝写入
            File.AppendAllText(logPath, content);
        }
        catch
        {
            // 日志写入失败不影响自检主流程
        }
    }

    /// <summary>自检目标选取：优先前台窗口，无效则枚举（跳过空标题窗口，避免选到系统残留窗口）。</summary>
    private static IntPtr FindSelfTestTarget()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && IsValidSelfTestTarget(foreground))
            return foreground;

        IntPtr found = IntPtr.Zero;
        // EnumWindows 为同步调用，委托在调用期间存活，无 GC 风险
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd) && IsValidSelfTestTarget(hwnd))
            {
                found = hwnd;
                return false; // 找到即停
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsValidSelfTestTarget(IntPtr hwnd)
    {
        if (!SelectionService.IsValidTarget(hwnd)) return false;
        if (string.IsNullOrWhiteSpace(NativeMethods.GetWindowText(hwnd))) return false;
        return true;
    }

    /// <summary>弹出托盘菜单（先给宿主窗口前台焦点，否则菜单点击外部不会关闭）。</summary>
    private void ShowTrayMenu()
    {
        if (_tray == null || _menu == null) return;

        SetForegroundWindow(_tray.HostHwnd);
        UpdateMenuState();
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    /// <summary>按当前置顶/选择状态刷新菜单项（第二项按 0/1/N 三种形态切换）。</summary>
    private void UpdateMenuState()
    {
        if (_pinManager == null || _menu == null || _selection == null) return;
        _menu.UpdateState(_selection.IsActive, _pinManager.GetPinnedWindows());
    }

    /// <summary>
    /// 退出流程：结束选择模式 → 取消所有置顶（用户已确认的默认行为，
    /// 系统恢复原状）→ 移除托盘图标 → 结束进程。
    /// </summary>
    private void ExitApplication()
    {
        _selection?.Stop();
        _pinManager?.UnpinAll();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pinManager?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
