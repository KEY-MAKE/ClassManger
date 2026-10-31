using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassManager.App.Models;
using MiniExcelLibs;

namespace ClassManager.App.Services;

/// <summary>
/// 导出服务实现：MiniExcel 双 sheet；「打印风」（白底黑格）矢量座位表的构建、PNG 导出与打印共用。
/// </summary>
public sealed class ExportService : IExportService
{
    /// <inheritdoc />
    public void ExportSeatXlsx(
        string filePath,
        SeatLayout layout,
        string className,
        DateTime generatedAt)
    {
        var seatSheet = new List<Dictionary<string, object?>>();
        for (var row = 0; row < layout.Rows; row++)
        {
            var rowData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["行\\列"] = $"第{row + 1}行",
            };

            for (var col = 0; col < layout.Cols; col++)
            {
                // 座位单元格只写学生姓名（空座位留空），与画布 / PNG / 矢量打印保持一致，不写分数。
                rowData[$"第{col + 1}列"] = layout.Get(row, col);
            }

            seatSheet.Add(rowData);
        }

        var infoSheet = new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal) { ["统计信息"] = $"生成时间: {generatedAt:yyyy-MM-dd HH:mm:ss}" },
            new(StringComparer.Ordinal) { ["统计信息"] = $"班级: {className}" },
            new(StringComparer.Ordinal) { ["统计信息"] = $"座位布局: {layout.Rows}行 × {layout.Cols}列" },
            new(StringComparer.Ordinal) { ["统计信息"] = $"总座位数: {layout.TotalSeats}" },
            new(StringComparer.Ordinal) { ["统计信息"] = $"已安排: {layout.OccupiedSeats}" },
            new(StringComparer.Ordinal) { ["统计信息"] = $"空座位: {layout.EmptySeats}" },
        };

        // 版式参数表：把影响排版的全部参数写进文件，导入时可原样还原
        var paramSheet = new List<Dictionary<string, object?>>();

        void AddParam(string key, object? value)
        {
            paramSheet.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [SeatSheetFormat.ParamKeyHeader] = key,
                [SeatSheetFormat.ParamValueHeader] = value,
            });
        }

        AddParam("格式标识", SeatSheetFormat.Marker);
        AddParam("行数", layout.Rows);
        AddParam("列数", layout.Cols);
        AddParam("显示表头", layout.ShowHeaders ? 1 : 0);

        for (var i = 0; i < layout.ColumnGaps.Count; i++)
        {
            AddParam($"列间距_{i + 1}", layout.ColumnGaps[i]);
        }

        for (var i = 0; i < layout.RowGaps.Count; i++)
        {
            AddParam($"行间距_{i + 1}", layout.RowGaps[i]);
        }

        // 讲台两侧的额外座位（伪坐标 row = -1），不在行列网格里，单独记录
        AddParam("讲台左侧位",
            layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol) ?? string.Empty);
        AddParam("讲台右侧位",
            layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol) ?? string.Empty);

        var sheets = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SeatSheetFormat.SeatSheetName] = seatSheet,
            [SeatSheetFormat.ParamSheetName] = paramSheet,
            [SeatSheetFormat.InfoSheetName] = infoSheet,
        };

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        MiniExcel.SaveAs(filePath, sheets, printHeader: true, excelType: ExcelType.XLSX);
    }

    /// <summary>横向 A4 纸宽度（96 DPI 逻辑像素：297mm）。</summary>
    public const double A4LandscapeWidth = 297d / 25.4d * 96d;

    /// <summary>横向 A4 纸高度（96 DPI 逻辑像素：210mm）。</summary>
    public const double A4LandscapeHeight = 210d / 25.4d * 96d;

    /// <inheritdoc />
    public (DrawingVisual Visual, Size Size) BuildSeatSheet(
        SeatLayout layout,
        string className,
        bool includeHeaders,
        bool includeClassName)
    {
        // ---------- 第一步：按「内容真实边界」绘制（不套用屏幕画布的最小宽高，
        //否则小布局会带着一圈空白参与缩放，导致在 A4 上缩不到最大） ----------
        var showClassName = includeClassName && !string.IsNullOrWhiteSpace(className);
        const double classNameArea = 40d;
        const double edgePad = 8d;
        var classNameH = showClassName ? classNameArea : 0d;

        // 边界 = 左侧排标签留白 + 网格 + 顶部留白（含表头）+ 讲台 + 底部留白
        var width = SeatMapGeometry.MarginX(includeHeaders) + SeatMapGeometry.GridWidth(layout) + edgePad;
        var height = classNameH
                     + SeatMapGeometry.MarginY(includeHeaders)
                     + SeatMapGeometry.GridHeight(layout)
                     + SeatMapGeometry.PlatformGap
                     + SeatMapGeometry.PlatformHeight
                     + edgePad;

        var content = new DrawingVisual();
        using (var dc = content.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(SeatMapPalette.ExportBackground), null, new Rect(0, 0, width, height));

            if (showClassName)
            {
                var classText = MakeText(className.Trim(), 16d, Brushes.Black, FontWeights.Bold);
                dc.DrawText(classText, new Point((width - classText.Width) / 2d, (classNameArea - classText.Height) / 2d));
            }

            // 网格按自然坐标绘制：xs[0] 已经是 MarginX，ys 顶行已经是 MarginY。
            DrawSeatGrid(dc, layout, includeHeaders, 0d, classNameH);
        }

        // ---------- 第二步：装到横向 A4 纸上，等比缩放到最大（全部元素保留） ----------
        var pageW = A4LandscapeWidth;
        var pageH = A4LandscapeHeight;
        var scale = Math.Min(pageW / width, pageH / height);

        var page = new DrawingVisual();
        using (var dc = page.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageW, pageH));

            dc.PushTransform(new TransformGroup
            {
                Children =
                {
                    // 先缩放，再平移到居中位置；TransformGroup 按 Children 顺序依次作用。
                    new ScaleTransform(scale, scale),
                    new TranslateTransform(
                        (pageW - width * scale) / 2d,
                        (pageH - height * scale) / 2d),
                },
            });
            dc.DrawDrawing(content.Drawing);
            dc.Pop();
        }

        return (page, new Size(pageW, pageH));
    }

    /// <inheritdoc />
    public void ExportSeatImage(
        string filePath,
        SeatLayout layout,
        string className,
        bool includeHeaders,
        bool includeClassName,
        bool jpeg = false)
    {
        const double baseDpi = 96d;
        const double scale = 2d;

        var sheet = BuildSeatSheet(layout, className, includeHeaders, includeClassName);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(sheet.Size.Width * scale),
            (int)Math.Ceiling(sheet.Size.Height * scale),
            baseDpi * scale,
            baseDpi * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(sheet.Visual);

        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    /// <summary>绘制座位网格（白底、只有黑色 1px 边框的方格），可选表头与排标签。</summary>
    private static void DrawSeatGrid(
        DrawingContext dc,
        SeatLayout layout,
        bool includeHeaders,
        double originX,
        double originY)
    {
        var xs = SeatMapGeometry.ColumnOffsets(layout, includeHeaders);
        var ys = SeatMapGeometry.RowOffsets(layout, includeHeaders);

        var ink = new SolidColorBrush(SeatMapPalette.ExportInk);
        var seatPen = new Pen(ink, 1d);
        seatPen.Freeze();
        var platformPen = new Pen(ink, 1.5d);
        platformPen.Freeze();

        if (includeHeaders)
        {
            // 列表头：每一列一个「第x列」（不再按间隙切「组」）
            var headerCenterY = SeatMapGeometry.MarginY(true) / 2d;
            for (var col = 0; col < layout.Cols; col++)
            {
                DrawCenteredText(
                    dc,
                    $"第{col + 1}列",
                    12d,
                    FontWeights.Normal,
                    ink,
                    new Rect(
                        originX + xs[col],
                        originY + headerCenterY - 10d,
                        SeatMapGeometry.SeatWidth,
                        20d));
            }

            // 排标签（第一排在最下方）
            var rightX = SeatMapGeometry.MarginX(true) - 8d;
            for (var row = 0; row < layout.Rows; row++)
            {
                var centerY = originY + ys[row] + SeatMapGeometry.SeatHeight / 2d;
                var label = MakeText($"第{row + 1}排", 12d, ink, FontWeights.Normal);
                dc.DrawText(label, new Point(originX + rightX - label.Width, centerY - label.Height / 2d));
            }
        }

        // 讲台（白底黑框黑字）
        var plat = SeatMapGeometry.Platform(layout, includeHeaders);
        var platRect = new Rect(originX + plat.X, originY + plat.Y, plat.Width, plat.Height);
        dc.DrawRectangle(Brushes.White, platformPen, platRect);
        DrawCenteredText(dc, "讲台", 14d, FontWeights.Bold, ink, platRect);

        // 座位：只有黑色 1px 边框、无填充；仅显示姓名。
        // 空位置不打印——连方格边框都不画。
        for (var row = 0; row < layout.Rows; row++)
        {
            for (var col = 0; col < layout.Cols; col++)
            {
                var student = layout.Get(row, col);
                if (student is null)
                {
                    continue;
                }

                DrawPrintedSeat(dc, seatPen, ink, student, originX + xs[col], originY + ys[row]);
            }
        }

        // 讲台左右两个侧位：有人才打印（同样空位不画）
        var platformSeats = SeatMapGeometry.PlatformSeats(layout, includeHeaders);
        DrawPlatformSeatIfOccupied(dc, seatPen, ink, layout, SeatLayout.PlatformSeatLeftCol, platformSeats.Left, originX, originY);
        DrawPlatformSeatIfOccupied(dc, seatPen, ink, layout, SeatLayout.PlatformSeatRightCol, platformSeats.Right, originX, originY);
    }

    private static void DrawPlatformSeatIfOccupied(
        DrawingContext dc,
        Pen seatPen,
        Brush ink,
        SeatLayout layout,
        int col,
        Rect rect,
        double originX,
        double originY)
    {
        var student = layout.Get(SeatLayout.PlatformSeatRow, col);
        if (student is null)
        {
            return;
        }

        DrawPrintedSeat(dc, seatPen, ink, student, originX + rect.X, originY + rect.Y);
    }

    /// <summary>绘制一个已排座座位（边框 + 居中姓名）。</summary>
    private static void DrawPrintedSeat(
        DrawingContext dc,
        Pen seatPen,
        Brush ink,
        string student,
        double x,
        double y)
    {
        dc.DrawRectangle(
            null,
            seatPen,
            new Rect(x, y, SeatMapGeometry.SeatWidth, SeatMapGeometry.SeatHeight));

        var name = MakeText(student, 12d, ink, FontWeights.Normal, SeatMapGeometry.SeatWidth - 12d);
        dc.DrawText(name, new Point(x + 6d, y + (SeatMapGeometry.SeatHeight - name.Height) / 2d));
    }

    /// <summary>在指定矩形内居中绘制文本（宽超出时自动折行）。</summary>
    private static void DrawCenteredText(
        DrawingContext dc,
        string text,
        double size,
        FontWeight weight,
        Brush brush,
        Rect area)
    {
        var formatted = MakeText(text, size, brush, weight, area.Width);
        var y = area.Y + (area.Height - formatted.Height) / 2d;
        dc.DrawText(formatted, new Point(area.X, y));
    }

    private static FormattedText MakeText(
        string text,
        double size,
        Brush brush,
        FontWeight weight,
        double maxWidth = double.PositiveInfinity)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            1.0);

        if (!double.IsPositiveInfinity(maxWidth))
        {
            formatted.MaxTextWidth = maxWidth;
            formatted.TextAlignment = TextAlignment.Center;
        }

        return formatted;
    }
}
