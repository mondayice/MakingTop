// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 图标工厂：一份图钉几何 → 托盘图标(HICON) / 选择模式光标(HCURSOR)。
/// 悬浮图标本体直接用 XAML 的 Path 绘制（矢量、任意 DPI 清晰）。
/// </summary>
internal static class IconFactory
{
    /// <summary>图钉设计尺寸（与 Material push_pin 路径一致的 24×24 坐标系）。</summary>
    private const double DesignSize = 24.0;

    /// <summary>把 24×24 的图钉路径渲染为 sizePx 见方的 BGRA 位图。</summary>
    /// <param name="sizePx">输出位图边长（物理像素）</param>
    /// <param name="fill">图钉填充色</param>
    /// <param name="fillRatio">图钉在位图中的占比（留边距，0.8 表示占 80%）</param>
    private static BitmapSource RenderPin(int sizePx, Color fill, double fillRatio)
    {
        var geometry = Geometry.Parse(Application.Current.Resources["PinGeometry"].ToString()!);

        // 24×24 设计坐标 → 缩放并居中到 sizePx 位图
        double s = sizePx * fillRatio / DesignSize;
        double offset = (sizePx - sizePx * fillRatio) / 2.0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(offset, offset));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(new SolidColorBrush(fill), null, geometry);
        }

        var rtb = new RenderTargetBitmap(sizePx, sizePx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        // 转为 BGRA32 供 CreateDIBSection 使用
        var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>
    /// 用 GDI CreateIconIndirect 把 BGRA 位图封装成 HICON（图标）或 HCURSOR（光标）。
    /// 32bpp alpha 通道直通，掩码位全 0 表示透明度完全由 alpha 决定。
    /// </summary>
    /// <param name="source">BGRA32 位图</param>
    /// <param name="isCursor">true=光标（需热点），false=图标</param>
    /// <param name="hotspotX">光标热点 X（仅 isCursor 时有效）</param>
    /// <param name="hotspotY">光标热点 Y（仅 isCursor 时有效）</param>
    private static IntPtr CreateIconHandle(BitmapSource source, bool isCursor,
        int hotspotX, int hotspotY)
    {
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        int bytesCount = w * h * 4;
        var pixels = new byte[bytesCount];
        source.CopyPixels(pixels, w * 4, 0);

        IntPtr hdc = GetDC(IntPtr.Zero);
        IntPtr hbmColor = IntPtr.Zero, hbmMask = IntPtr.Zero, result = IntPtr.Zero;
        try
        {
            // 自顶向下 32bpp BI_RGB 位图
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            hbmColor = CreateDIBSection(hdc, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (hbmColor == IntPtr.Zero) return IntPtr.Zero;
            Marshal.Copy(pixels, 0, bits, bytesCount);

            // 单色掩码全 0：透明度交给 alpha 通道
            int maskStride = ((w + 15) / 16) * 2; // 单行掩码字节数（1bpp，按 WORD 对齐）
            var maskBits = new byte[maskStride * h];
            hbmMask = CreateBitmap(w, h, 1, 1, maskBits);
            if (hbmMask == IntPtr.Zero) return IntPtr.Zero;

            var info = new ICONINFO
            {
                fIcon = isCursor ? 0 : 1, // 0=光标 1=图标
                xHotspot = hotspotX,
                yHotspot = hotspotY,
                hbmMask = hbmMask,
                hbmColor = hbmColor
            };
            result = CreateIconIndirect(ref info);
        }
        finally
        {
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
            ReleaseDC(IntPtr.Zero, hdc);
        }
        return result;
    }

    /// <summary>生成托盘图标 HICON（32×32 橙色图钉、透明底）。</summary>
    public static IntPtr CreateTrayIconHandle()
    {
        var bmp = RenderPin(32, Color.FromRgb(0xE1, 0x62, 0x59), fillRatio: 0.80);
        return CreateIconHandle(bmp, isCursor: false, 0, 0);
    }

    /// <summary>
    /// 生成选择模式的橙色图钉光标 HCURSOR（32×32）。
    /// 热点设在图钉针尖处（位图底部中心），拾取坐标即针尖指向，符合直觉。
    /// </summary>
    public static IntPtr CreatePinCursorHandle()
    {
        var bmp = RenderPin(32, Color.FromRgb(0xE1, 0x62, 0x59), fillRatio: 1.00);
        // 针尖位于 24×24 设计坐标 (12, 21.5)，满幅渲染到 32px 后 ≈ (16, 28.7) → 取 (16, 28)
        return CreateIconHandle(bmp, isCursor: true, 16, 28);
    }
}
