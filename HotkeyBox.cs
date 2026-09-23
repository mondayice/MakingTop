// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  All Rights Reserved.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MakingTop;

/// <summary>
/// 快捷键录制框（Apple 风格）：
/// - 点击进入录制态（橙色边框 + "按下组合键…"）
/// - 按下任意「修饰键 + 键」完成绑定；单独 F1~F24 也允许
/// - Esc 取消；Backspace 清除绑定
/// - ComboText 为序列化文本（如 "Ctrl+Alt+S"），空 = 未绑定
/// </summary>
public class HotkeyBox : Control
{
    public static readonly DependencyProperty ComboTextProperty = DependencyProperty.Register(
        nameof(ComboText), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HotkeyBox)d).OnComboTextChanged()));

    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.Register(
        nameof(IsRecording), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    public static readonly DependencyProperty HasBindingProperty = DependencyProperty.Register(
        nameof(HasBinding), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    /// <summary>当前绑定文本（"Ctrl+Alt+S" 或空串）。</summary>
    public string ComboText
    {
        get => (string)GetValue(ComboTextProperty);
        set => SetValue(ComboTextProperty, value);
    }

    /// <summary>是否处于录制态（驱动模板视觉）。</summary>
    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    /// <summary>是否已有绑定（驱动模板视觉）。</summary>
    public bool HasBinding
    {
        get => (bool)GetValue(HasBindingProperty);
        set => SetValue(HasBindingProperty, value);
    }

    /// <summary>绑定文本变化（用户完成录制或清除时触发，供外部立即应用）。</summary>
    public event Action? ComboTextChanged;

    private TextBlock? _text;
    private Button? _clearButton;
    private bool _showHint; // 提示非法组合时，按键后自动消失

    static HotkeyBox()
    {
        FocusableProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(true));
    }

    public HotkeyBox()
    {
        Cursor = Cursors.Hand;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _text = GetTemplateChild("Text") as TextBlock;
        _clearButton = GetTemplateChild("PART_Clear") as Button;
        if (_clearButton != null)
        {
            _clearButton.Click += (_, e) =>
            {
                e.Handled = true;
                ComboText = string.Empty; // 触发 OnComboTextChanged → 状态刷新 + 通知外部
            };
        }
        UpdateVisual();
    }

    // ---------- 录制交互 ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Focus(); // 进入录制态（GotKeyboardFocus 驱动）
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _recording = true;
        IsRecording = true;
        UpdateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _recording = false;
        IsRecording = false;
        _showHint = false;
        UpdateVisual();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_recording) return;
        e.Handled = true;

        // e.Key 对修饰键/系统键的表示不规范，统一取 SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            // 取消录制，不做任何修改
            _showHint = false;
            MoveFocusAway();
            return;
        }

        if ((key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Backspace/Delete：清除绑定
            _showHint = false;
            ComboText = string.Empty;
            return;
        }

        if (IsPureModifier(key))
            return; // 还没按下非修饰键，等待

        var mods = Keyboard.Modifiers;
        bool isFKey = key is >= Key.F1 and <= Key.F24;
        if (mods == ModifierKeys.None && !isFKey)
        {
            // 全局热键不允许绑定普通单键（否则正常打字全被劫持）
            _showHint = true;
            UpdateVisual("需包含 Ctrl/Alt/Shift/Win 或使用 F 功能键");
            return;
        }

        uint winMods = 0;
        if (mods.HasFlag(ModifierKeys.Control)) winMods |= HotkeyCombo.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt)) winMods |= HotkeyCombo.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) winMods |= HotkeyCombo.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) winMods |= HotkeyCombo.MOD_WIN;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        ComboText = HotkeyCombo.Serialize(winMods, (uint)vk);
        _showHint = false;
        MoveFocusAway();
    }

    private bool _recording;

    private static bool IsPureModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private void MoveFocusAway()
    {
        // 结束录制：直接清除 WPF 键盘焦点即可（Esc 不承担关闭窗口职责，
        // 不需要把焦点交还窗口，也不会落到下一个录制框）
        Keyboard.ClearFocus();
    }

    // ---------- 视觉同步 ----------

    private void OnComboTextChanged()
    {
        HasBinding = !string.IsNullOrEmpty(ComboText);
        UpdateVisual();
        ComboTextChanged?.Invoke();
    }

    /// <summary>根据状态刷新展示文本与颜色。</summary>
    private void UpdateVisual(string? temporaryHint = null)
    {
        if (_text == null) return;

        if (_recording && temporaryHint != null && _showHint)
        {
            _text.Text = temporaryHint;
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0xE1, 0x62, 0x59)); // 提示用主橙
            _text.FontWeight = FontWeights.Normal;
            return;
        }

        if (_recording)
        {
            _text.Text = "按下组合键，Esc 取消";
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0xE1, 0x62, 0x59));
            _text.FontWeight = FontWeights.Normal;
            return;
        }

        if (!string.IsNullOrEmpty(ComboText))
        {
            var display = HotkeyCombo.TryParse(ComboText, out var combo)
                ? combo.Display
                : ComboText;
            _text.Text = display;
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x35, 0x2F));
            _text.FontWeight = FontWeights.Medium;
        }
        else
        {
            _text.Text = "点击录制";
            _text.Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0x9A, 0x97));
            _text.FontWeight = FontWeights.Normal;
        }
    }
}
