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
        public bool TopmostPending;  // 置顶请求在途：跳过 TOPMOST 位校验与 z 序维护
    }

    private readonly Dictionary<IntPtr, PinEntry> _pins = new();
    private readonly List<IntPtr> _pinOrder = new(); // 置顶顺序（托盘子菜单列表按此展示）
    private readonly Dispatcher _dispatcher;
    // 关键：原生回调委托必须用字段保住引用，否则被 GC 回收后
    // 系统回调进入已回收的委托会直接 FailFast 崩溃（已踩坑）
    private readonly WinEventDelegate _winEventProc;
    private readonly IntPtr _hookMinMax;     // [最小化开始 .. 最小化结束]
    private readonly IntPtr _hookObj;        // [销毁 .. 位置变化]（含层级 REORDER）
    private readonly IntPtr _hookForeground; // [系统前台变化]（激活是层级被重排的高发时刻）
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
        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>窗口已置顶则取消，否则置顶（选择模式的切换语义）。</summary>
    public void TogglePin(IntPtr hwnd)
    {
        if (_pins.ContainsKey(hwnd))
            UnpinWindow(hwnd, "toggle");
        else
            PinWindow(hwnd);
    }

    /// <summary>置顶窗口并创建左上角悬浮图钉图标。</summary>
    public void PinWindow(IntPtr hwnd)
    {
        if (_pins.ContainsKey(hwnd)) return;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return;

        DiagLog.Write(string.Format("pin 0x{0:X} '{1}' fg=0x{2:X}",
            hwnd.ToInt64(), GetWindowText(hwnd), GetForegroundWindow().ToInt64()));

        // 1) 悬浮图标（XAML 中初始位置在屏幕外，Show 后立即定位，避免闪现）
        var overlay = new PinOverlayWindow(this, hwnd);
        overlay.Show();

        var entry = new PinEntry { Overlay = overlay, TopmostPending = true };
        _pins[hwnd] = entry;
        _pinOrder.Add(hwnd);
        _lastPinnedHwnd = hwnd;
        LastPinnedTitle = GetWindowText(hwnd);

        // 2) 先定位悬浮图标（在途状态下不做 TOPMOST 位校验 / z 序维护）
        UpdateEntryNow(hwnd, entry);

        // 3) 置顶请求放到线程池执行：attach 后的同步 SetWindowPos 在目标线程
        //    僵死时会长时间阻塞，绝不能卡住本工具的 UI 线程。
        Task.Run(() =>
        {
            bool ok = TryBringToTopmost(hwnd);
            _dispatcher.BeginInvoke(() => OnTopmostAttemptFinished(hwnd, ok));
        });

        PinsChanged?.Invoke();
    }

    /// <summary>
    /// 把窗口置入 TOPMOST 带，返回 TOPMOST 位是否确实置上。
    /// 系统前台锁会按场景静默吞掉 TOPMOST 请求（返回 TRUE 但层级与样式位都不变），
    /// 且压制行为随目标窗口的输入状态而变，没有单一调用方式能覆盖全部场景（实测）：
    /// - 目标「刚被用户切离前台」（瞬态）：plain 调用被吞，AttachThreadInput 后同步写入可破；
    /// - 目标离开前台已久（如先开稻壳阅读器、隔一阵才置顶）：plain 直接生效，
    ///   反而 AttachThreadInput 会因调用线程并入目标的非激活输入队列而被吞。
    /// 因此固定按「plain → attach → 异步兜底」顺序尝试，任一步复核到位即停。
    /// </summary>
    private static bool TryBringToTopmost(IntPtr hwnd)
    {
        // 1) plain 同步写入并立刻复核：调用进程拥有前台 / 目标瞬态已过期时直接生效
        if (SyncTopmost(hwnd)) return true;

        // 2) attach 目标 GUI 线程共享输入状态后再同步写入（破「刚被切走」的瞬态压制）。
        //    attach 后的同步 SetWindowPos 在目标线程僵死时会长时间阻塞，
        //    本方法整体跑在线程池线程上，绝不能这样卡住 UI 线程。
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = targetThread != 0 && targetThread != currentThread
            && AttachThreadInput(currentThread, targetThread, true);
        if (attached)
        {
            try
            {
                if (SyncTopmost(hwnd)) return true;
            }
            finally
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }

        // 3) 兜底：目标线程可能暂时繁忙，异步投递再试一次（尽力而为）
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        DiagLog.Write(string.Format("pin 0x{0:X}: plain+attach both swallowed, async fallback sent", hwnd.ToInt64()));
        return false;
    }

    /// <summary>同步写入 TOPMOST 并复核样式位：被前台锁静默吞掉时位不会出现。</summary>
    private static bool SyncTopmost(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    }

    /// <summary>
    /// 置顶尝试完成：成功则恢复常规校验并立即对齐 z 序。
    /// 失败则进入渐进退避重试（250/250/500/500/1000/1000/2000ms，总窗口约 7 秒）：
    /// 目标线程繁忙与前台锁压制都是瞬态的，快速放弃会把「偶发全链失败」变成用户看到的
    /// 「点击置顶后立即取消」；期间图钉保持显示（TopmostPending），重试成功即自愈。
    /// 每轮重试在工作线程执行（plain → attach），同步 SWP 遇到繁忙目标线程时绝不冻结 UI。
    /// 全部轮次失败才收起图钉（reason=retry-exhausted）。
    /// </summary>
    private void OnTopmostAttemptFinished(IntPtr hwnd, bool ok)
    {
        if (!_pins.TryGetValue(hwnd, out var entry)) return;
        if (ok)
        {
            entry.TopmostPending = false;
            UpdateEntryNow(hwnd, entry);
            return;
        }

        int[] delays = { 250, 250, 500, 500, 1000, 1000, 2000 };
        int attempt = 0;
        int inFlight = 0; // 上一轮重试未返回时跳过本 tick，防止线程池任务堆积
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delays[0]) };
        retry.Tick += (_, _) =>
        {
            if (!_pins.TryGetValue(hwnd, out _)) { retry.Stop(); return; }
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return;

            Task.Run(() =>
            {
                bool done = RetryOnce(hwnd);
                _dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref inFlight, 0);
                    if (!_pins.TryGetValue(hwnd, out var current)) { retry.Stop(); return; }
                    if (done)
                    {
                        retry.Stop();
                        current.TopmostPending = false;
                        UpdateEntryNow(hwnd, current);
                        return;
                    }
                    attempt++;
                    if (attempt >= delays.Length)
                    {
                        retry.Stop();
                        DiagLog.Write(string.Format("pin 0x{0:X}: retries exhausted, giving up", hwnd.ToInt64()));
                        UnpinWindow(hwnd, "retry-exhausted");
                    }
                    else
                    {
                        retry.Interval = TimeSpan.FromMilliseconds(delays[attempt]);
                    }
                });
            });
        };
        retry.Start();
    }

    /// <summary>单轮重试：plain 失败再走 attach（「刚被切走」压制只有 attach 能破）。</summary>
    private static bool RetryOnce(IntPtr hwnd)
    {
        if (SyncTopmost(hwnd)) return true;

        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = targetThread != 0 && targetThread != currentThread
            && AttachThreadInput(currentThread, targetThread, true);
        if (attached)
        {
            try
            {
                if (SyncTopmost(hwnd)) return true;
            }
            finally
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
        return false;
    }

    /// <summary>取消置顶：关闭悬浮图标 + 目标窗口恢复普通层级。</summary>
    public void UnpinWindow(IntPtr hwnd) => UnpinWindow(hwnd, "user");

    internal void UnpinWindow(IntPtr hwnd, string reason)
    {
        if (!_pins.Remove(hwnd, out var entry)) return;
        _pinOrder.Remove(hwnd);
        DiagLog.Write(string.Format("unpin 0x{0:X} ({1})", hwnd.ToInt64(), reason));

        entry.Overlay.CloseOverlay();

        // 目标窗口还活着且仍带 TOPMOST 位 → 恢复普通层级（异步投递防卡死）。
        // 异步 NOTOPMOST 与目标线程存在 z 序竞态，窗口偶发被压到普通带内更低位置，
        // 用户预期是「取消置顶 = 回到置顶前的相对位置（普通带顶部）」，
        // 因此稍后复核，被压下去就拉回（见 ScheduleNormalBandCheck）。
        if (IsWindow(hwnd) && (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            ScheduleNormalBandCheck(hwnd, 2);
        }

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
            UnpinWindow(hwnd, "unpin-all");
    }

    /// <summary>取消最近一次置顶的窗口（托盘菜单「取消当前选中窗口置顶」）。</summary>
    public void UnpinLast()
    {
        if (_lastPinnedHwnd != IntPtr.Zero)
            UnpinWindow(_lastPinnedHwnd, "unpin-last");
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
            DiagLog.Write(string.Format("target 0x{0:X} destroyed", hwnd.ToInt64()));
            RemoveEntry(hwnd);
            return;
        }

        if (evt == EVENT_SYSTEM_FOREGROUND)
        {
            // 目标窗口被激活：系统可能把它提到 TOPMOST 带顶部（盖过图钉），立即校验
            QueueUpdate(hwnd);
            return;
        }

        // 移动/缩放/层级/最小化/显示隐藏 → 合并成一次 Dispatcher 更新
        QueueUpdate(hwnd);
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

        // 目标窗口的 TOPMOST 位被外部（其他工具/Alt+Esc 等）清掉 → 尊重用户意图，静默清理。
        // 置顶请求在途时跳过：位尚未置上是正常中间态，不代表用户取消。
        if (!entry.TopmostPending && (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
        {
            DiagLog.Write(string.Format("pin 0x{0:X}: topmost bit cleared externally", hwnd.ToInt64()));
            UnpinWindow(hwnd, "topmost-bit-cleared");
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

        // 目标窗口矩形左上角（屏幕物理像素）。锚定「窗口」而非「客户区」：
        // 客户区在记事本等带菜单栏的程序里位于菜单栏之下，图钉会盖住编辑内容；
        // 窗口左上角对应标题栏区域，图钉不会遮挡正文。
        if (!GetWindowRect(hwnd, out var winRect)) return;
        int anchorX = winRect.Left, anchorY = winRect.Top;

        // 按目标窗口所在显示器的 DPI 缩放 24×24 图标与 6px 内边距
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        int size = (int)Math.Round(24 * scale);              // 图钉本体物理尺寸（视觉不变）
        int winSize = size * 2;                              // 48×48 画布：四周留给脉冲光环特效
        int offset = (int)Math.Round(6 * scale) - size / 2;  // 画布外扩半幅，图钉左上角仍在锚点 +6px

        var rect = new RECT
        {
            Left = anchorX + offset,
            Top = anchorY + offset,
            Right = anchorX + offset + winSize,
            Bottom = anchorY + offset + winSize
        };

        // 物理矩形无变化时不重定位（消除冗余绘制，杜绝拖影）
        if (!RectEquals(rect, entry.LastRect))
        {
            entry.LastRect = rect;
            entry.Overlay.MoveToPhysical(rect.Left, rect.Top, winSize, winSize);
        }

        // 目标尚未进入 TOPMOST 带时跳过 z 序维护：此时把 overlay 插到目标上方
        // 会把 overlay 自己拽出 TOPMOST 带（插入点在普通带内）
        if (!entry.TopmostPending)
            EnsureZOrder(hwnd, entry);
    }

    /// <summary>
    /// 保证悬浮图标紧贴在目标窗口正上方（Z 序）。
    /// 判据：目标窗口正上方的窗口（GW_HWNDPREV）就是 overlay —— 已正确则不做任何事，避免事件风暴。
    /// 实现上移动的是「我们自己的 overlay 窗口」而不是目标窗口：
    /// 同线程同步操作，立即生效，也彻底避免跨进程 SetWindowPos 的阻塞/异步失败问题。
    /// 写入后复核不变式；激活引发的带内重排存在竞态，最多重试 3 次。
    /// </summary>
    private void EnsureZOrder(IntPtr hwnd, PinEntry entry)
    {
        var overlayHwnd = entry.Overlay.Hwnd;
        if (overlayHwnd == IntPtr.Zero || !IsWindow(overlayHwnd)) return;
        if (GetWindow(hwnd, GW_HWNDPREV) == overlayHwnd) return; // 已在正确位置

        for (int attempt = 0; attempt < 3; attempt++)
        {
            // 把 overlay 插到 target 的上一个窗口下方 = target 正上方；
            // target 已是 TOPMOST band 顶部（其上无窗口）时直接放到带顶部。
            IntPtr above = GetWindow(hwnd, GW_HWNDPREV);
            SetWindowPos(overlayHwnd, above == IntPtr.Zero ? HWND_TOP : above, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            if (GetWindow(hwnd, GW_HWNDPREV) == overlayHwnd) return; // 复核通过
        }
    }

    /// <summary>
    /// 取消置顶后的位置守卫：确认目标窗口确实位于普通带顶部（其上只剩 TOPMOST 带窗口）。
    /// 不在顶部（异步竞态压下去 / 目标应用自己乱插 z 序）就同步补一刀
    /// （HWND_NOTOPMOST 幂等，无副作用），仍失败则放弃（尽力而为）。
    /// </summary>
    private void ScheduleNormalBandCheck(IntPtr hwnd, int attempts)
    {
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            // 窗口没了、又被置顶（用户/其他工具操作）、或已重新置顶管理 → 不干预
            if (!IsWindow(hwnd)
                || (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0
                || _pins.ContainsKey(hwnd)) return;

            if (IsTopOfNormalBand(hwnd)) return;

            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (attempts > 1) ScheduleNormalBandCheck(hwnd, attempts - 1);
        };
        check.Start();
    }

    /// <summary>
    /// 窗口是否位于普通带顶部：z 序上目标之上不存在更高的可见非 TOPMOST 顶层窗口
    /// （DWM 伪装的 UWP 幽灵窗口与本进程自身窗口不算，它们不影响用户的层级观感）。
    /// </summary>
    internal static bool IsTopOfNormalBand(IntPtr hwnd)
    {
        IntPtr current = GetWindow(hwnd, GW_HWNDFIRST);
        int guard = 0;
        while (current != IntPtr.Zero && current != hwnd && guard++ < 64)
        {
            if (IsWindowVisible(current) && !IsIconic(current)
                && (GetWindowLongW(current, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
            {
                GetWindowThreadProcessId(current, out uint pid);
                bool cloaked = pid != (uint)Environment.ProcessId
                    && DwmGetWindowAttribute(current, DWMWA_CLOAKED, out int c, sizeof(int)) == 0
                    && c != 0;
                if (!cloaked) return false;
            }
            current = GetWindow(current, GW_HWNDNEXT);
        }
        return true;
    }

    private static bool RectEquals(in RECT a, in RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    // ---------------- 悬浮图标回调 ----------------

    /// <summary>悬浮图标被点击（缩小动画已完成）→ 取消对应窗口置顶并关闭图标。</summary>
    internal void OnOverlayClicked(IntPtr hwnd) => UnpinWindow(hwnd, "overlay-click");

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
        if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
        UnpinAll();
    }
}
