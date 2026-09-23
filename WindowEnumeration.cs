// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  All Rights Reserved.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 枚举系统中可管理的顶层窗口（供置顶管理面板展示全部窗口）。
/// 过滤：不可见 / 本进程自身 / 桌面与任务栏 / DWM 伪装的 UWP 幽灵窗口 / 无标题窗口。
/// 返回顺序为系统 Z 序（靠前的在前）。
/// </summary>
internal static class WindowEnumeration
{
    public static IReadOnlyList<(IntPtr Hwnd, string Title, bool Iconic)> GetAll()
    {
        var list = new List<(IntPtr Hwnd, string Title, bool Iconic)>();

        // EnumWindows 为同步调用，委托在调用期间存活，无 GC 回收风险
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (!SelectionService.IsValidTarget(hwnd)) return true;

            // DWM 伪装窗口（已关闭但残留的 UWP 宿主等）跳过
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked != 0)
                return true;

            var title = GetWindowText(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            list.Add((hwnd, title, IsIconic(hwnd)));
            return true;
        }, IntPtr.Zero);

        return list;
    }
}
