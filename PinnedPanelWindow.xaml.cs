// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MakingTop;

/// <summary>
/// 置顶管理面板（Apple 风格 × Notion 橙）：
/// 列出系统中全部可管理的顶层窗口，每行一个 Apple 开关实时反映置顶状态
/// （开=置顶，关=取消）。置顶窗口排在最前并带橙色圆点标记，最小化窗口有后缀标注。
/// 置顶状态变化（任何来源）都会即时同步开关；窗口开关集合变化时才重建列表。
/// </summary>
public partial class PinnedPanelWindow : Window
{
    private readonly PinManager _mgr;
    private readonly Dictionary<IntPtr, ToggleButton> _switches = new();
    private HashSet<IntPtr> _listed = new();
    private bool _refreshing; // 重建/同步开关状态时屏蔽开关事件

    internal PinnedPanelWindow(PinManager mgr)
    {
        InitializeComponent();
        _mgr = mgr;
        _mgr.PinsChanged += Refresh;
        Closed += (_, _) => _mgr.PinsChanged -= Refresh;

        Loaded += (_, _) => UiFx.OpenIn(WindowCard);
        Refresh();
    }

    /// <summary>按当前系统窗口与置顶状态刷新面板。</summary>
    private void Refresh()
    {
        var pinned = _mgr.GetPinnedWindows();
        var pinnedSet = new HashSet<IntPtr>(pinned.Select(p => p.Hwnd));
        var all = WindowEnumeration.GetAll();

        // 排序：置顶窗口在前（按置顶顺序），其余按系统 Z 序
        var ordered = new List<(IntPtr Hwnd, string Title, bool Iconic, bool IsPinned)>();
        foreach (var p in pinned)
        {
            var info = all.FirstOrDefault(w => w.Hwnd == p.Hwnd);
            string title = info.Hwnd == IntPtr.Zero || string.IsNullOrEmpty(info.Title) ? p.Title : info.Title;
            ordered.Add((p.Hwnd, title, info.Iconic, true));
        }
        foreach (var w in all)
        {
            if (!pinnedSet.Contains(w.Hwnd))
                ordered.Add((w.Hwnd, w.Title, w.Iconic, false));
        }

        CountText.Text = $"已置顶 {pinned.Count} / {all.Count} 个窗口";
        UnpinAllButton.IsEnabled = pinned.Count > 0;
        EmptyState.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 窗口集合未变化 → 只原位同步开关状态（避免重建打断交互/滚动）
        var newSet = new HashSet<IntPtr>(ordered.Select(o => o.Hwnd));
        if (newSet.SetEquals(_listed))
        {
            SyncSwitchesSilently(pinnedSet);
            return;
        }

        // 窗口集合变化 → 全量重建
        _listed = newSet;
        _switches.Clear();
        _refreshing = true;
        try
        {
            RowsPanel.Children.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                var (hwnd, title, iconic, isPinned) = ordered[i];

                // 行间发丝线
                if (i > 0)
                {
                    RowsPanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xE7)),
                        Margin = new Thickness(14, 0, 14, 0),
                    });
                }

                var row = new Grid { Height = 42, Margin = new Thickness(8, 2, 8, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 行内容：置顶橙点 + 标题（最小化后缀）
                var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (isPinned)
                {
                    titlePanel.Children.Add(new Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Fill = new SolidColorBrush(Color.FromRgb(0xE1, 0x62, 0x59)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 7, 0),
                    });
                }
                string display = iconic ? $"{Truncate(title, 24)}（最小化）" : Truncate(title, 26);
                titlePanel.Children.Add(new TextBlock
                {
                    Text = display,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x35, 0x2F)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = iconic ? $"{title}（最小化）" : title,
                });
                row.Children.Add(titlePanel);

                // Apple 开关：开=置顶 关=取消
                var sw = new ToggleButton
                {
                    Style = (Style)FindResource("AppleSwitch"),
                    IsChecked = isPinned,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var captured = hwnd;
                sw.Checked += (_, _) => { if (!_refreshing) _mgr.PinWindow(captured); };
                sw.Unchecked += (_, _) => { if (!_refreshing) _mgr.UnpinWindow(captured); };
                Grid.SetColumn(sw, 1);
                row.Children.Add(sw);

                _switches[hwnd] = sw;
                RowsPanel.Children.Add(row);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>原位同步各开关的勾选状态（置 true 屏蔽事件，不引发置顶操作）。</summary>
    private void SyncSwitchesSilently(HashSet<IntPtr> pinnedSet)
    {
        _refreshing = true;
        try
        {
            foreach (var (hwnd, sw) in _switches)
            {
                bool shouldBeOn = pinnedSet.Contains(hwnd);
                if (sw.IsChecked != shouldBeOn)
                    sw.IsChecked = shouldBeOn;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string Truncate(string title, int max)
    {
        var t = title.Trim().ReplaceLineEndings(" ");
        if (t.Length == 0) return "(无标题窗口)";
        return t.Length > max ? t[..max] + "…" : t;
    }

    private void OnUnpinAllClick(object sender, RoutedEventArgs e) => _mgr.UnpinAll();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWithFade();

    internal void CloseWithFade() => UiFx.CloseOut(WindowCard, Close);

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithFade();
        }
    }
}
