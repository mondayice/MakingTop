// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  All Rights Reserved.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;

namespace MakingTop;

/// <summary>
/// 托盘右键菜单（Notion 风格）。
/// 第二项「取消置顶」三种形态：
///   0 个置顶 → 禁用；
///   1 个置顶 → 点击直接取消；
///   多个置顶 → hover 展开子列表列出全部置顶窗口，点击某项取消对应窗口。
/// </summary>
public partial class TrayContextMenu : ContextMenu
{
    /// <summary>请求开始/结束选择模式。</summary>
    public event Action? StartSelectionRequested;

    /// <summary>请求取消最近置顶的窗口（仅一个置顶窗口时）。</summary>
    public event Action? UnpinLastRequested;

    /// <summary>请求取消指定窗口的置顶（子列表点击时，参数为目标窗口句柄）。</summary>
    public event Action<IntPtr>? UnpinWindowRequested;

    /// <summary>请求取消所有置顶。</summary>
    public event Action? UnpinAllRequested;

    /// <summary>请求打开设置窗口。</summary>
    public event Action? SettingsRequested;

    /// <summary>请求退出程序。</summary>
    public event Action? ExitRequested;

    /// <summary>第二项当前形态：0=禁用 1=单窗口叶子项 N=子列表头部。</summary>
    private int _pinnedCount;

    public TrayContextMenu()
    {
        InitializeComponent();
        // 统一在第二项的冒泡 Click 里分发：
        // MenuItem.Click 是冒泡事件，子列表项点击会冒泡到第二项，用 OriginalSource 区分。
        UnpinItem.Click += OnUnpinItemClick;
    }

    /// <summary>
    /// 按当前状态刷新菜单（打开菜单前调用）。
    /// </summary>
    /// <param name="selecting">是否正处于选择模式</param>
    /// <param name="pinned">当前全部置顶窗口（按置顶顺序，句柄+标题）</param>
    public void UpdateState(bool selecting, IReadOnlyList<(IntPtr Hwnd, string Title)> pinned)
    {
        StartItem.Header = selecting ? "取消选择（进行中，按 Esc 退出）" : "开始选择置顶窗口";

        _pinnedCount = pinned.Count;
        UnpinItem.Items.Clear();
        UnpinItem.IsEnabled = pinned.Count > 0;

        switch (pinned.Count)
        {
            case 0:
                UnpinItem.Header = "取消当前选中窗口置顶";
                break;

            case 1:
                // 单窗口：保持叶子项，点击直接取消
                UnpinItem.Header = $"取消置顶：{Truncate(pinned[0].Title)}";
                break;

            default:
                // 多窗口：变为子列表头部，hover 展开全部置顶窗口
                UnpinItem.Header = $"取消置顶窗口（{pinned.Count} 个）";
                foreach (var (hwnd, title) in pinned)
                {
                    UnpinItem.Items.Add(new MenuItem
                    {
                        Header = $"取消置顶：{Truncate(title)}",
                        Tag = hwnd, // 点击时经 Tag 取回目标窗口句柄
                    });
                }
                break;
        }

        UnpinAllItem.IsEnabled = pinned.Count > 0;
    }

    /// <summary>第二项及子列表的统一点击分发。</summary>
    private void OnUnpinItemClick(object sender, RoutedEventArgs e)
    {
        // 点击的是子列表中的窗口项 → 取消该窗口置顶
        if (e.OriginalSource is MenuItem clicked && !ReferenceEquals(clicked, UnpinItem))
        {
            if (clicked.Tag is IntPtr hwnd)
                UnpinWindowRequested?.Invoke(hwnd);
            return;
        }

        // 点击的是第二项本身：仅在单窗口叶子形态下执行（多窗口时点它只展开列表，不做动作）
        if (_pinnedCount == 1)
            UnpinLastRequested?.Invoke();
    }

    /// <summary>标题截断展示（过长加省略号，空标题给占位文案）。</summary>
    private static string Truncate(string title)
    {
        var t = title.Trim().ReplaceLineEndings(" ");
        if (t.Length == 0) return "(无标题窗口)";
        return t.Length > 24 ? t[..24] + "…" : t;
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
        => StartSelectionRequested?.Invoke();

    private void OnUnpinAllClick(object sender, RoutedEventArgs e)
        => UnpinAllRequested?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke();

    private void OnExitClick(object sender, RoutedEventArgs e)
        => ExitRequested?.Invoke();
}
