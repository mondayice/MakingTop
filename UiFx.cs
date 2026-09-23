// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MakingTop;

/// <summary>窗口开合动画（Apple 风格：fade + 轻微缩放，ease-out，仅 transform/opacity）。</summary>
internal static class UiFx
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>窗口打开：150ms 淡入 + 0.96→1 缩放。</summary>
    public static void OpenIn(FrameworkElement card)
    {
        card.Opacity = 0;
        var scale = new ScaleTransform(0.96, 0.96);
        card.RenderTransform = scale;
        card.RenderTransformOrigin = new Point(0.5, 0.5);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = EaseOut };
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = EaseOut };
        card.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>窗口关闭：120ms 淡出 + 缩小到 0.98，完成后回调真正关闭。</summary>
    public static void CloseOut(FrameworkElement card, Action done)
    {
        var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = EaseOut };
        var shrink = new DoubleAnimation(1, 0.98, TimeSpan.FromMilliseconds(120)) { EasingFunction = EaseOut };

        if (card.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
        fade.Completed += (_, _) => done();
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
