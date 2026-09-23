// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows.Threading;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 置顶管理核心：维护「目标窗口 → 悬浮图标」注册表。
/// 通过 WinEvent 钩子以事件驱动方式处理移动/缩放/最小化/销毁/层级变化——零轮询，
/// 空闲时 CPU 占用为 0；重定位经 Dispatcher 脏标记合并，一帧最多执行一次，无拖影。
/// </summary>
internal sealed class PinManager : IDisposable
{
    private sealed class PinEntry
    {
        public PinOverlayWindow Overlay = null!;
        public RECT LastRect;        // 上次已应用的物理矩形（无变化不重定位，防拖影）
        public bool OverlayHidden;   // 目标最小化/隐藏期间悬浮图标被暂时藏起
        public bool UpdateQueued;    // Dispatcher 合并标志（本帧已排队）
    }

    private readonly Dictionary<IntPtr, PinEntry> _pins = new();
    private readonly List<IntPtr> _pinOrder = new(); // 置顶顺序（托盘子菜单列表按此展示）
    private readonly Dispatcher _dispatcher;
    // 关键：原生回调委托必须用字段保住引用，否则被 GC 回收后
    // 系统回调进入已回收的委托会直接 FailFast 崩溃（已踩坑）
    private readonly WinEventDelegate _winEventProc;
    private readonly IntPtr _hookMinMax; // [最小化开始 .. 最小化结束]
    private readonly IntPtr _hookObj;    // [销毁 .. 位置变化]（含层级 REORDER）
    private IntPtr _lastPinnedHwnd;

    /// <summary>最近置顶窗口的标题（供托盘菜单动态显示）。</summary>
    public string? LastPinnedTitle { get; private set; }

    public int Count => _pins.Count;
    public bool AnyPinned => _pins.Count > 0;

    /// <summary>置顶集合变化（供托盘菜单刷新状态）。</summary>
    public event Action? PinsChanged;

    public PinManager()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _winEventProc = WinEventProc;

        // OUTOFCONTEXT：事件回调经本线程消息循环投递（在 UI 线程执行）；
        // SKIPOWNPROCESS：忽略本进程自身窗口的事件（悬浮图标/托盘宿主）。
        _hookMinMax = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _hookObj = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>窗口已置顶则取消，否则置顶（选择模式的切换语义）。</summary>
    public void TogglePin(IntPtr hwnd)
    {
        if (_pins.ContainsKey(hwnd))
            UnpinWindow(hwnd);
        else
            PinWindow(hwnd);
    }

    /// <summary>置顶窗口并创建左上角悬浮图钉图标。</summary>
    public void PinWindow(IntPtr hwnd)
    {
        if (_pins.ContainsKey(hwnd)) return;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return;

        // 1) 置顶（不改变位置大小、不抢激活；异步投递避免目标进程繁忙时卡住本工具）
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

        // 2) 悬浮图标（XAML 中初始位置在屏幕外，Show 后立即定位，避免闪现）
        var overlay = new PinOverlayWindow(this, hwnd);
        overlay.Show();

        var entry = new PinEntry { Overlay = overlay };
        _pins[hwnd] = entry;
        _pinOrder.Add(hwnd);
        _lastPinnedHwnd = hwnd;
        LastPinnedTitle = GetWindowText(hwnd);

        // 3) 定位到客户区左上角 + 维护 z 序（图标必须盖在目标窗口正上方）
        UpdateEntryNow(hwnd, entry);
        PinsChanged?.Invoke();
    }

    /// <summary>取消置顶：关闭悬浮图标 + 目标窗口恢复普通层级。</summary>
    public void UnpinWindow(IntPtr hwnd)
    {
        if (!_pins.Remove(hwnd, out var entry)) return;
        _pinOrder.Remove(hwnd);

        entry.Overlay.CloseOverlay();

        // 目标窗口还活着且仍带 TOPMOST 位 → 恢复普通层级（异步投递防卡死）
        if (IsWindow(hwnd) && (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

        if (_lastPinnedHwnd == hwnd)
        {
            _lastPinnedHwnd = IntPtr.Zero;
            LastPinnedTitle = null;
        }
        PinsChanged?.Invoke();
    }

    /// <summary>取消所有置顶（此后所有悬浮图标自动全部消失）。</summary>
    public void UnpinAll()
    {
        // 拷贝一份再遍历：UnpinWindow 会修改字典
        foreach (var hwnd in _pins.Keys.ToArray())
            UnpinWindow(hwnd);
    }

    /// <summary>取消最近一次置顶的窗口（托盘菜单「取消当前选中窗口置顶」）。</summary>
    public void UnpinLast()
    {
        if (_lastPinnedHwnd != IntPtr.Zero)
            UnpinWindow(_lastPinnedHwnd);
    }

    /// <summary>供自检：指定窗口的悬浮图标是否真实存在且可见。</summary>
    internal bool IsOverlayAliveFor(IntPtr hwnd)
    {
        return GetOverlayHwndFor(hwnd) != IntPtr.Zero;
    }

    /// <summary>供自检：取指定窗口的悬浮图标句柄（未置顶返回 Zero）。</summary>
    internal IntPtr GetOverlayHwndFor(IntPtr hwnd)
    {
        return _pins.TryGetValue(hwnd, out var entry)
            && entry.Overlay.Hwnd != IntPtr.Zero
            && IsWindow(entry.Overlay.Hwnd)
            && entry.Overlay.IsVisible
                ? entry.Overlay.Hwnd
                : IntPtr.Zero;
    }

    /// <summary>
    /// 当前全部置顶窗口（按置顶先后排序，标题实时读取）。
    /// 供托盘菜单第二项在多窗口时展开子列表。
    /// </summary>
    internal IReadOnlyList<(IntPtr Hwnd, string Title)> GetPinnedWindows()
    {
        var list = new List<(IntPtr Hwnd, string Title)>(_pinOrder.Count);
        foreach (var hwnd in _pinOrder)
        {
            if (_pins.ContainsKey(hwnd) && IsWindow(hwnd))
                list.Add((hwnd, GetWindowText(hwnd)));
        }
        return list;
    }

    // ---------------- WinEvent 回调（UI 线程） ----------------

    private void WinEventProc(IntPtr hHook, uint evt, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        // 只关心目标顶层窗口本身（idObject=OBJID_WINDOW 且非子控件）
        if (idObject != OBJID_WINDOW || idChild != 0) return;
        if (!_pins.ContainsKey(hwnd)) return; // 字典 O(1) 早退，全局事件开销极小

        if (evt == EVENT_OBJECT_DESTROY)
        {
            // 目标窗口销毁：此刻窗口已不可用，只做自身状态清理
            RemoveEntry(hwnd);
        }
        else
        {
            // 移动/缩放/层级/最小化/显示隐藏 → 合并成一次 Dispatcher 更新
            QueueUpdate(hwnd);
        }
    }

    private void QueueUpdate(IntPtr hwnd)
    {
        var entry = _pins[hwnd];
        if (entry.UpdateQueued) return; // 已排队：本帧内重复事件全部合并，杜绝拖影
        entry.UpdateQueued = true;
        _dispatcher.BeginInvoke(() =>
        {
            entry.UpdateQueued = false;
            if (_pins.TryGetValue(hwnd, out var current))
                UpdateEntryNow(hwnd, current);
        }, DispatcherPriority.Input);
    }

    private void RemoveEntry(IntPtr hwnd)
    {
        if (!_pins.Remove(hwnd, out var entry)) return;
        _pinOrder.Remove(hwnd);
        entry.Overlay.CloseOverlay();
        if (_lastPinnedHwnd == hwnd)
        {
            _lastPinnedHwnd = IntPtr.Zero;
            LastPinnedTitle = null;
        }
        PinsChanged?.Invoke();
    }

    /// <summary>把悬浮图标同步到目标窗口的当前状态（位置/尺寸/可见性/z 序）。</summary>
    private void UpdateEntryNow(IntPtr hwnd, PinEntry entry)
    {
        if (!IsWindow(hwnd))
        {
            RemoveEntry(hwnd);
            return;
        }

        // 目标窗口的 TOPMOST 位被外部（其他工具/Alt+Esc 等）清掉 → 尊重用户意图，静默清理
        if ((GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
        {
            UnpinWindow(hwnd);
            return;
        }

        // 最小化或被隐藏 → 暂时藏起悬浮图标（还原时会自动恢复显示）
        bool visible = IsWindowVisible(hwnd) && !IsIconic(hwnd);
        if (!visible)
        {
            if (!entry.OverlayHidden)
            {
                entry.OverlayHidden = true;
                entry.Overlay.SetOverlayVisible(false);
            }
            return;
        }
        if (entry.OverlayHidden)
        {
            entry.OverlayHidden = false;
            entry.Overlay.SetOverlayVisible(true);
        }

        // 目标窗口客户区原点（客户坐标 (0,0)）换算到屏幕物理像素
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);

        // 按目标窗口所在显示器的 DPI 缩放 24×24 图标与 6px 内边距
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        int size = (int)Math.Round(24 * scale);
        int offset = (int)Math.Round(6 * scale);

        var rect = new RECT
        {
            Left = origin.X + offset,
            Top = origin.Y + offset,
            Right = origin.X + offset + size,
            Bottom = origin.Y + offset + size
        };

        // 物理矩形无变化时不重定位（消除冗余绘制，杜绝拖影）
        if (!RectEquals(rect, entry.LastRect))
        {
            entry.LastRect = rect;
            entry.Overlay.MoveToPhysical(rect.Left, rect.Top, size, size);
        }

        EnsureZOrder(hwnd, entry);
    }

    /// <summary>
    /// 保证悬浮图标紧贴在目标窗口正上方（Z 序）。
    /// 判据：目标窗口正上方的窗口（GW_HWNDPREV）就是 overlay —— 已正确则不做任何事，避免事件风暴。
    /// 实现上移动的是「我们自己的 overlay 窗口」而不是目标窗口：
    /// 同线程同步操作，立即生效，也彻底避免跨进程 SetWindowPos 的阻塞/异步失败问题。
    /// </summary>
    private void EnsureZOrder(IntPtr hwnd, PinEntry entry)
    {
        var overlayHwnd = entry.Overlay.Hwnd;
        if (overlayHwnd == IntPtr.Zero || !IsWindow(overlayHwnd)) return;
        if (GetWindow(hwnd, GW_HWNDPREV) == overlayHwnd) return; // 已在正确位置

        // 把 overlay 插到 target 的上一个窗口下方 = target 正上方；
        // target 已是 TOPMOST band 顶部（其上无窗口）时直接放到带顶部。
        IntPtr above = GetWindow(hwnd, GW_HWNDPREV);
        SetWindowPos(overlayHwnd, above == IntPtr.Zero ? HWND_TOP : above, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private static bool RectEquals(in RECT a, in RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    // ---------------- 悬浮图标回调 ----------------

    /// <summary>悬浮图标被点击（缩小动画已完成）→ 取消对应窗口置顶并关闭图标。</summary>
    internal void OnOverlayClicked(IntPtr hwnd) => UnpinWindow(hwnd);

    /// <summary>悬浮图标 DPI 变化（跨显示器拖动）→ 重算物理尺寸与位置。</summary>
    internal void OnOverlayDpiChanged(IntPtr hwnd)
    {
        if (_pins.ContainsKey(hwnd))
            QueueUpdate(hwnd);
    }

    public void Dispose()
    {
        if (_hookMinMax != IntPtr.Zero) UnhookWinEvent(_hookMinMax);
        if (_hookObj != IntPtr.Zero) UnhookWinEvent(_hookObj);
        UnpinAll();
    }
}
