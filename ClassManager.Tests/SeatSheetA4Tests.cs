using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 座位表「装到横向 A4 纸并等比缩放到最大」的行为验证。
/// 会真实渲染 DrawingVisual 并逐像素扫描，测量内容包围盒，
/// 以证明：① 纸张是横向 A4 ② 缩放取到最大（至少一边贴到纸边）③ 没有任何元素被裁切。
/// </summary>
public class SeatSheetA4Tests
{
    private const double ExpectedPageWidth = 297d / 25.4d * 96d;   // 1122.5197
    private const double ExpectedPageHeight = 210d / 25.4d * 96d;  // 793.7008

    [Fact]
    public void SeatSheet_IsA4Landscape_FillsPageMaximally_AndNeverClips()
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                Core();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }

    private static void Core()
    {
        var layout = new SeatLayout();
        layout.Resize(6, 8);

        var names = new[] { "姜秀英", "韩晶", "周洋", "魏傅", "韩平", "孙秀英", "吴桂英", "包娜" };
        for (var i = 0; i < names.Length; i++)
        {
            layout.Set(i, 0, names[i]);
        }

        var service = new ExportService();
        var (visual, size) = service.BuildSeatSheet(layout, "学生成绩单", includeHeaders: true, includeClassName: true);

        // ① 纸张必须是横向 A4
        Assert.Equal(ExpectedPageWidth, size.Width, 3);
        Assert.Equal(ExpectedPageHeight, size.Height, 3);

        // ② 真实渲染并测量内容包围盒
        var (bbox, pageW, pageH) = MeasureInk(visual, (int)Math.Round(size.Width), (int)Math.Round(size.Height));

        Assert.True(bbox.Width > 0 && bbox.Height > 0, "渲染结果没有任何可见内容");

        // ③ 不裁切：内容必须完整落在纸内
        Assert.True(bbox.Left >= 0, $"内容左溢出纸面：left={bbox.Left}");
        Assert.True(bbox.Top >= 0, $"内容上溢出纸面：top={bbox.Top}");
        Assert.True(bbox.Right <= pageW, $"内容右溢出纸面：right={bbox.Right} > {pageW}");
        Assert.True(bbox.Bottom <= pageH, $"内容下溢出纸面：bottom={bbox.Bottom} > {pageH}");

        // ④ 缩放取到最大：至少有一边贴到纸边（容差 24px = 边缘留白 + 抗锯齿）
        const int tolerance = 24;
        var fillsWidth = pageW - bbox.Right <= tolerance;
        var fillsHeight = pageH - bbox.Bottom <= tolerance;
        Assert.True(
            fillsWidth || fillsHeight,
            $"缩放未取到最大：内容 {bbox.Width}x{bbox.Height}，纸面 {pageW}x{pageH}，"
            + $"右留白={pageW - bbox.Right}，下留白={pageH - bbox.Bottom}，两侧都留白过多");
    }

    /// <summary>渲染视觉对象，返回「非白像素」的包围盒与像素尺寸。</summary>
    private static (Rect Box, int PageWidth, int PageHeight) MeasureInk(DrawingVisual visual, int w, int h)
    {
        var bitmap = new RenderTargetBitmap(w, h, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = w * 4;
        var pixels = new byte[bitmap.PixelHeight * stride];
        bitmap.CopyPixels(pixels, stride, 0);

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x++)
            {
                var i = row + (x * 4);
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var a = pixels[i + 3];

                // 白底黑线：忽略接近纯白且不透明的像素
                if (a < 32 || (r > 238 && g > 238 && b > 238))
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < 0)
        {
            return (new Rect(0, 0, 0, 0), w, h);
        }

        return (new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1), w, h);
    }
}