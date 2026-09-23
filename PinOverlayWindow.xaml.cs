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
using System.Windows.Shapes;
using System.Windows.Threading;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 悬浮图钉窗口：显示在「被置顶窗口」客户区左上角的图钉。
/// - 48×48 画布居中一枚 24×24 图钉，四周为脉冲光环 + 呼吸辉光（光环不参与命中测试）
/// - 出入场为果冻感：首次定位后弹性缩放落定（ElasticEase）；取消置顶时先压扁再收缩淡出
/// - 点击不激活（WS_EX_NOACTIVATE）、不进任务栏和 Alt+Tab（WS_EX_TOOLWINDOW）
/// - 位置由 PinManager 通过 SetWindowPos 以物理像素驱动（跟随移动/缩放）
/// </summary>
public partial class PinOverlayWindow : Window
{
    private readonly PinManager _mgr;
    private readonly IntPtr _target;
    private bool _fired;          // 防止点击动画期间重复触发
    private bool _entrancePlayed; // 果冻入场只在首次定位时播放一次
    private bool _closing;        // 出场动画期间忽略点击

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
        PlayEntrance();
    }

    /// <summary>显隐切换（目标窗口最小化时隐藏，还原时恢复）。</summary>
    public void SetOverlayVisible(bool visible)
    {
        if (visible) Show();
        else Hide();
    }

    /// <summary>取消置顶：果冻出场（压扁 → 收缩淡出）完成后真正关闭。</summary>
    public void CloseOverlay()
    {
        if (_closing) return;
        _closing = true;
        StopFxLoop();

        // 入场淡入是 HoldEnd 时钟：不摘掉的话，出场淡入段开始前不透明度会掉回基值 0（闪隐）
        double currentOpacity = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = currentOpacity;

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        // squash & stretch 果冻离场：挤压预备（70ms）→ 收缩消失（120ms）
        var scaleX = new DoubleAnimationUsingKeyFrames();
        scaleX.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.Zero));
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, TimeSpan.FromMilliseconds(70)) { EasingFunction = easeOut });
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame(0.4, TimeSpan.FromMilliseconds(190)) { EasingFunction = easeIn });
        PressScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);

        var scaleY = new DoubleAnimationUsingKeyFrames();
        scaleY.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.Zero));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(0.75, TimeSpan.FromMilliseconds(70)) { EasingFunction = easeOut });
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(0.4, TimeSpan.FromMilliseconds(190)) { EasingFunction = easeIn });
        PressScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        var fade = new DoubleAnimation(currentOpacity, 0, TimeSpan.FromMilliseconds(120))
        {
            BeginTime = TimeSpan.FromMilliseconds(70),
            EasingFunction = easeIn,
        };
        BeginAnimation(OpacityProperty, fade);

        // 兜底关闭：无论动画时钟状态如何，取消置顶后 overlay 必须真正关闭
        var closer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        closer.Tick += (_, _) => { closer.Stop(); Close(); };
        closer.Start();
    }

    /// <summary>果冻入场：整体淡入 + 图钉 0.3→1 弹性缩放落定，随后启动四周特效循环。</summary>
    private void PlayEntrance()
    {
        if (_entrancePlayed) return;
        _entrancePlayed = true;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
        var jelly = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(520))
        {
            // ElasticEase = 果冻落定：冲过头再回弹 3 次
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 3, Springiness = 4 },
            FillBehavior = FillBehavior.Stop, // 落定后交还基值 1，不锁死后续按压动画
        };
        BeginAnimation(OpacityProperty, fadeIn);
        PressScale.BeginAnimation(ScaleTransform.ScaleXProperty, jelly);
        PressScale.BeginAnimation(ScaleTransform.ScaleYProperty, jelly);

        StartFxLoop();
    }

    /// <summary>四周特效：双脉冲光环（错相 800ms 连续波纹）+ 呼吸辉光，仅 transform/opacity。</summary>
    private void StartFxLoop()
    {
        ScaleTransform[] scales = { Ring1Scale, Ring2Scale };
        Ellipse[] rings = { Ring1, Ring2 };
        TimeSpan[] begins = { TimeSpan.Zero, TimeSpan.FromMilliseconds(800) };

        for (int i = 0; i < scales.Length; i++)
        {
            var expand = new DoubleAnimation(0.55, 1.95, TimeSpan.FromMilliseconds(1600))
            {
                BeginTime = begins[i],
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                RepeatBehavior = RepeatBehavior.Forever,
            };
            scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, expand);

            // 波纹质感：快速显现 → 随扩散衰减
            var ripple = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = begins[i],
                RepeatBehavior = RepeatBehavior.Forever,
            };
            ripple.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
            ripple.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            ripple.KeyFrames.Add(new EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1600))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
            rings[i].BeginAnimation(OpacityProperty, ripple);
        }

        var glow = new DoubleAnimation(0.35, 0.7, TimeSpan.FromMilliseconds(1500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        PinGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glow);
    }

    /// <summary>停止四周特效并恢复基值（取消置顶时调用）。</summary>
    private void StopFxLoop()
    {
        ScaleTransform[] scales = { Ring1Scale, Ring2Scale };
        Ellipse[] rings = { Ring1, Ring2 };
        for (int i = 0; i < scales.Length; i++)
        {
            scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rings[i].BeginAnimation(OpacityProperty, null);
            rings[i].Opacity = 0;
        }
        PinGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
        PinGlow.Opacity = 0;
    }

    /// <summary>点击图钉：先播 100ms 缩小反馈动画（ease-out），随后取消窗口置顶。</summary>
    private void OnPinClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_fired || _closing) return;
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
