// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 悬浮图钉窗口：显示在「被置顶窗口」客户区左上角的 24×24 小图标。
/// - 点击不激活（WS_EX_NOACTIVATE）、不进任务栏和 Alt+Tab（WS_EX_TOOLWINDOW）
/// - 位置由 PinManager 通过 SetWindowPos 以物理像素驱动（跟随移动/缩放）
/// - 点击：100ms 缩小反馈动画 → 通知 PinManager 取消该窗口置顶
/// </summary>
public partial class PinOverlayWindow : Window
{
    private readonly PinManager _mgr;
    private readonly IntPtr _target;
    private bool _fired; // 防止动画期间重复触发

    /// <summary>本窗口的原生句柄（供 z 序维护/物理定位使用）。</summary>
    public IntPtr Hwnd { get; private set; }

    internal PinOverlayWindow(PinManager mgr, IntPtr target)
    {
        InitializeComponent();
        _mgr = mgr;
        _target = target;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;

        // 追加扩展样式：点击不抢焦点（目标窗口保持激活）、不进 Alt+Tab
        int exStyle = GetWindowLongW(Hwnd, GWL_EXSTYLE);
        SetWindowLongW(Hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>跨显示器拖动导致 DPI 变化 → 通知 PinManager 重算物理尺寸。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _mgr.OnOverlayDpiChanged(_target);
    }

    /// <summary>以物理像素定位/定尺寸（绕过 WPF 的 DIP 坐标，多显示器/负坐标都精确）。</summary>
    public void MoveToPhysical(int x, int y, int w, int h)
    {
        if (Hwnd == IntPtr.Zero) return;
        SetWindowPos(Hwnd, IntPtr.Zero, x, y, w, h,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
    }

    /// <summary>显隐切换（目标窗口最小化时隐藏，还原时恢复）。</summary>
    public void SetOverlayVisible(bool visible)
    {
        if (visible) Show();
        else Hide();
    }

    public void CloseOverlay() => Close();

    /// <summary>点击图钉：先播 100ms 轻微缩小动画（ease-out），随后取消窗口置顶。</summary>
    private void OnPinClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_fired) return;
        _fired = true;

        var shrink = new DoubleAnimation(1, 0.85, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        shrink.Completed += (_, _) => _mgr.OnOverlayClicked(_target);
        PressScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        PressScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }
}
