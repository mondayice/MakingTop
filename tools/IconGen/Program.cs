// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具 —— 程序图标生成工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  All Rights Reserved.
//
// 用 WPF 渲染图钉图形（与主程序同一份矢量路径），输出多尺寸 BMP 帧的 .ico 文件。
// 用法: dotnet run --project tools/IconGen -- <输出路径 Assets/app.ico>
// -----------------------------------------------------------------------------

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    /// <summary>图钉矢量路径（24×24 设计坐标，与主程序 Theme.xaml 保持一致）。</summary>
    private const string PinPath =
        "M16 9V4h1c.55 0 1-.45 1-1s-.45-1-1-1H7c-.55 0-1 .45-1 1s.45 1 1 1h1v5c0 1.66-1.34 3-3 3v2h5.97v7l1 1 1-1v-7H19v-2c-1.66 0-3-1.34-3-3z";

    /// <summary>ICO 内包含的尺寸帧。</summary>
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    private const double FillRatio = 0.80; // 图钉在画布中的占比（与运行时托盘图标一致）

    [STAThread]
    private static int Main(string[] args)
    {
        string outPath = args.Length > 0
            ? args[0]
            : Path.Combine("..", "..", "..", "..", "Assets", "app.ico");

        var geometry = Geometry.Parse(PinPath);
        geometry.Freeze();
        var brush = new SolidColorBrush(Color.FromRgb(0xE1, 0x62, 0x59)); // Notion 橙 #E16259
        brush.Freeze();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        var frames = new List<(int Size, byte[] Bmp)>();
        foreach (int size in Sizes)
            frames.Add((size, RenderBmp(geometry, brush, size)));

        File.WriteAllBytes(outPath, BuildIco(frames));
        Console.WriteLine($"已生成: {Path.GetFullPath(outPath)}（{frames.Count} 个尺寸帧）");
        return 0;
    }

    /// <summary>渲染单帧并编码为 ICO 的 BMP 数据（BGRA + 全零 AND 掩码）。</summary>
    private static byte[] RenderBmp(Geometry geometry, Brush brush, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double s = size * FillRatio / 24.0;
            double offset = (size - size * FillRatio) / 2.0;
            dc.PushTransform(new TranslateTransform(offset, offset));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(brush, null, geometry);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var bgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();

        var pixels = new byte[size * size * 4];
        bgra.CopyPixels(pixels, size * 4, 0);

        int maskStride = ((size + 31) / 32) * 4; // AND 掩码 1bpp 行宽（4 字节对齐）
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            // BITMAPINFOHEADER（biHeight = 像素高 ×2，含 XOR + AND 两段）
            w.Write(40u);
            w.Write(size);
            w.Write(size * 2);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(0u);                                   // BI_RGB
            w.Write((uint)(size * size * 4 + maskStride * size));
            w.Write(0); w.Write(0); w.Write(0u); w.Write(0u);

            // XOR 像素：BGRA 自底向上
            for (int y = size - 1; y >= 0; y--)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    w.Write(pixels[i]);     // B
                    w.Write(pixels[i + 1]); // G
                    w.Write(pixels[i + 2]); // R
                    w.Write(pixels[i + 3]); // A
                }
            }

            // AND 掩码：全 0，透明度完全交给 alpha 通道
            w.Write(new byte[maskStride * size]);
        }
        return ms.ToArray();
    }

    /// <summary>组装 ICO 文件（ICONDIR + ICONDIRENTRY×N + 图像数据）。</summary>
    private static byte[] BuildIco(List<(int Size, byte[] Bmp)> frames)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write((ushort)0);                  // reserved
            w.Write((ushort)1);                  // type = icon
            w.Write((ushort)frames.Count);

            int offset = 6 + 16 * frames.Count;
            foreach (var (size, bmp) in frames)
            {
                w.Write((byte)(size >= 256 ? 0 : size)); // 宽（256 记 0）
                w.Write((byte)(size >= 256 ? 0 : size)); // 高
                w.Write((byte)0);                        // 调色板数
                w.Write((byte)0);                        // reserved
                w.Write((ushort)1);                      // 颜色平面
                w.Write((ushort)32);                     // 位深
                w.Write((uint)bmp.Length);
                w.Write((uint)offset);
                offset += bmp.Length;
            }

            foreach (var (_, bmp) in frames)
                w.Write(bmp);
        }
        return ms.ToArray();
    }
}
