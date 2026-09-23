// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Windows.Threading;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 窗口选择模式：
/// - 进入时临时替换系统光标为橙色图钉（针尖为热点），退出时从注册表整体还原
/// - WH_MOUSE_LL 捕获下一次左键点击（吞掉，防止误触目标窗口内容），
///   WindowFromPoint → GA_ROOT 找到顶层窗口 → 交给 PinManager 切换置顶
/// - WH_KEYBOARD_LL 捕获 Esc 取消选择
/// - 单发模式：一次点击完成即退出；钩子回调只做最少工作，重活甩给 UI 线程
/// </summary>
internal sealed class SelectionService : IDisposable
{
    // 替换这些常用系统光标，保证选择模式下任意窗口上光标都是橙色图钉
    private static readonly int[] ReplacedCursorIds =
    {
        OCR_NORMAL, OCR_IBEAM, OCR_CROSS, OCR_SIZENWSE, OCR_SIZENESW,
        OCR_SIZEWE, OCR_SIZENS, OCR_SIZEALL, OCR_NO, OCR_HAND, OCR_APPSTARTING
    };

    private readonly PinManager _mgr;
    private readonly HookProc _mouseProc; // 防委托被 GC
    private readonly HookProc _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;

    /// <summary>是否处于选择模式（供托盘菜单显示状态）。</summary>
    public bool IsActive { get; private set; }

    /// <summary>选择模式开/关（供菜单刷新文案）。</summary>
    public event Action? StateChanged;

    public SelectionService(PinManager mgr)
    {
        _mgr = mgr;
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    public void Toggle()
    {
        if (IsActive) Stop();
        else Start();
    }

    public void Start()
    {
        if (IsActive) return;
        IsActive = true;

        // 橙色图钉光标：临时替换系统光标（SetSystemCursor 会接管句柄，
        // 因此每个 ID 都要单独生成一份；退出时 SPI_SETCURSORS 整体还原）
        foreach (var id in ReplacedCursorIds)
        {
            IntPtr cursor = IconFactory.CreatePinCursorHandle();
            if (cursor != IntPtr.Zero)
            {
                if (!SetSystemCursor(cursor, id))
                    DestroyIcon(cursor); // 调用失败时系统未接管句柄，自行销毁防泄漏
            }
        }

        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, GetModuleHandleW(null), 0);
        _keyHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyProc, GetModuleHandleW(null), 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
        {
            // 任一钩子安装失败：立即回滚。否则光标是图钉但点击不被拦截，
            // 用户的"选择点击"会真实作用到目标应用（可能误触危险按钮）。
            Stop();
            return;
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;

        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }

        RestoreSystemCursors();
        StateChanged?.Invoke();
    }

    /// <summary>从注册表重载系统光标（无论中途发生什么都能干净还原）。</summary>
    internal static void RestoreSystemCursors()
        => SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

    // ---------------- 低级鼠标钩子 ----------------

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN)
            {
                // 钩子回调必须快进快出：只取坐标，拾取逻辑交给 UI 线程
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = data.pt;
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    () => HandlePick(pt), DispatcherPriority.Send);
                return (IntPtr)1; // 吞掉按下事件：选择点击不传递给目标窗口
            }
            if (msg == WM_LBUTTONUP)
            {
                return (IntPtr)1; // 吞掉配对的抬起，避免目标窗口收到孤立的抬起
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>命中测试：光标物理坐标 → 顶层窗口 → 切换置顶 → 结束选择模式。</summary>
    private void HandlePick(POINT pt)
    {
        if (!IsActive) return; // 快速双击会入队多次：第一次已 Stop，后续入队的拾取直接丢弃
        IntPtr hwnd = WindowFromPoint(pt);
        hwnd = GetAncestor(hwnd, GA_ROOT); // 命中的可能是子窗口，取顶层根窗口
        if (IsValidTarget(hwnd))
            _mgr.TogglePin(hwnd);
        Stop(); // 单发模式：一次点击完成即退出选择模式
    }

    /// <summary>排除桌面、任务栏和本进程自身窗口，其余可见顶层窗口都是合法目标。</summary>
    internal static bool IsValidTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
            return false;

        // 本进程自身窗口（悬浮图标、托盘宿主）不可选
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId)
            return false;

        // 系统外壳窗口不可选（桌面/任务栏）
        switch (GetClassName(hwnd))
        {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
                return false;
        }
        return true;
    }

    // ---------------- 低级键盘钩子 ----------------

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == VK_ESCAPE)
                {
                    // Esc 取消选择（吞掉该按键，不传递给任何窗口）
                    Dispatcher.CurrentDispatcher.BeginInvoke(Stop, DispatcherPriority.Send);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        // 异常兜底路径同样会走到这里：确保系统光标还原
        Stop();
    }
}
