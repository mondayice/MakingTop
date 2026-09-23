// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  All Rights Reserved.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MakingTop;

/// <summary>
/// 设置窗口（Apple 风格 × Notion 橙）：
/// 四个全局快捷键的录制与即时应用，配置持久化到 %APPDATA%\MakingTop\settings.json。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HotkeyService _hotkeys;
    private readonly Action _openPanel;
    private bool _loading = true; // 初始回填时不触发重复应用

    internal SettingsWindow(AppSettings settings, HotkeyService hotkeys, Action openPanel)
    {
        InitializeComponent();
        _settings = settings;
        _hotkeys = hotkeys;
        _openPanel = openPanel;

        // 回填已保存的绑定
        SelectBox.ComboText = settings.HotkeySelect ?? string.Empty;
        UnpinCurrentBox.ComboText = settings.HotkeyUnpinCurrent ?? string.Empty;
        UnpinAllBox.ComboText = settings.HotkeyUnpinAll ?? string.Empty;
        ShowPanelBox.ComboText = settings.HotkeyShowPanel ?? string.Empty;

        // 绑定变化 → 立即注册热键 + 持久化
        SelectBox.ComboTextChanged += () => OnComboChanged(
            SelectBox, HotkeyService.IdSelect, v => settings.HotkeySelect = v, SelectStatus);
        UnpinCurrentBox.ComboTextChanged += () => OnComboChanged(
            UnpinCurrentBox, HotkeyService.IdUnpinCurrent, v => settings.HotkeyUnpinCurrent = v, UnpinCurrentStatus);
        UnpinAllBox.ComboTextChanged += () => OnComboChanged(
            UnpinAllBox, HotkeyService.IdUnpinAll, v => settings.HotkeyUnpinAll = v, UnpinAllStatus);
        ShowPanelBox.ComboTextChanged += () => OnComboChanged(
            ShowPanelBox, HotkeyService.IdShowPanel, v => settings.HotkeyShowPanel = v, ShowPanelStatus);

        // 首次打开时显示当前注册状态（可能存在启动时注册失败的冲突）
        RefreshStatus(SelectBox, HotkeyService.IdSelect, SelectStatus);
        RefreshStatus(UnpinCurrentBox, HotkeyService.IdUnpinCurrent, UnpinCurrentStatus);
        RefreshStatus(UnpinAllBox, HotkeyService.IdUnpinAll, UnpinAllStatus);
        RefreshStatus(ShowPanelBox, HotkeyService.IdShowPanel, ShowPanelStatus);

        Loaded += (_, _) => UiFx.OpenIn(WindowCard);
        _loading = false;
    }

    private void OnComboChanged(HotkeyBox box, int hotkeyId, Action<string?> save, System.Windows.Controls.TextBlock status)
    {
        if (_loading) return;
        var (ok, text) = _hotkeys.Apply(hotkeyId, box.ComboText);
        save(box.ComboText);
        _settings.Save();
        status.Text = text;
        status.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x9B, 0x9A, 0x97))
            : new SolidColorBrush(Color.FromRgb(0xD1, 0x4D, 0x41)); // 冲突提示用深橙红
    }

    private void RefreshStatus(HotkeyBox box, int hotkeyId, System.Windows.Controls.TextBlock status)
    {
        // 空绑定直接显示"未绑定"，非空重新注册一次以拿到真实状态
        if (string.IsNullOrEmpty(box.ComboText))
        {
            status.Text = "未绑定";
            return;
        }
        var (ok, text) = _hotkeys.Apply(hotkeyId, box.ComboText);
        status.Text = text;
        status.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x9B, 0x9A, 0x97))
            : new SolidColorBrush(Color.FromRgb(0xD1, 0x4D, 0x41));
    }

    private void OnOpenPanelClick(object sender, RoutedEventArgs e)
    {
        _openPanel();
        CloseWithFade();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        // 逐个清空：ComboTextChanged 会完成注册解绑 + 持久化
        SelectBox.ComboText = string.Empty;
        UnpinCurrentBox.ComboText = string.Empty;
        UnpinAllBox.ComboText = string.Empty;
        ShowPanelBox.ComboText = string.Empty;
    }

    private void OnRestoreDefaultClick(object sender, RoutedEventArgs e)
    {
        // 恢复默认组合：同样经由 ComboTextChanged 完成注册 + 持久化
        SelectBox.ComboText = AppSettings.DefaultSelect;
        UnpinCurrentBox.ComboText = AppSettings.DefaultUnpinCurrent;
        UnpinAllBox.ComboText = AppSettings.DefaultUnpinAll;
        ShowPanelBox.ComboText = AppSettings.DefaultShowPanel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWithFade();

    private void CloseWithFade() => UiFx.CloseOut(WindowCard, Close);

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // 说明：设置窗口不响应 Esc 关闭——Esc 专门用于取消快捷键录制，
    // 关闭请使用「完成」按钮或右上角圆形 ✕。
}
