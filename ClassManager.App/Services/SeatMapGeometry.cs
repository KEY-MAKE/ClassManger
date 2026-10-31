using System.Windows;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>
/// 座位表几何计算：画布（SeatMapControl）与导出 / 打印（ExportService）共用同一套坐标与尺寸，
/// 保证屏幕上看到的位置与导出结果一致。所有坐标以「座位表左上角为原点」。
/// </summary>
public static class SeatMapGeometry
{
    /// <summary>座位矩形宽（像素）。</summary>
    public const double SeatWidth = 110d;

    /// <summary>座位矩形高（像素）。</summary>
    public const double SeatHeight = 60d;

    /// <summary>显示表头时的左侧留白（给排标签）。</summary>
    public const double HeaderMarginX = 90d;

    /// <summary>显示表头时的顶部留白（给分组表头）。</summary>
    public const double HeaderMarginY = 70d;

    /// <summary>不显示表头时的左侧留白。</summary>
    public const double PlainMarginX = 20d;

    /// <summary>不显示表头时的顶部留白。</summary>
    public const double PlainMarginY = 20d;

    /// <summary>讲台高度（像素）。</summary>
    public const double PlatformHeight = 56d;

    /// <summary>讲台与第一排底边的间隔（像素）。</summary>
    public const double PlatformGap = 40d;

    /// <summary>画布底部留白（像素）。</summary>
    public const double BottomPadding = 60d;

    /// <summary>画布最小宽度。</summary>
    public const double MinCanvasWidth = 1200d;

    /// <summary>画布最小高度。</summary>
    public const double MinCanvasHeight = 800d;

    /// <summary>左侧留白（随是否显示表头切换）。</summary>
    public static double MarginX(bool showHeaders) => showHeaders ? HeaderMarginX : PlainMarginX;

    /// <summary>顶部留白（随是否显示表头切换）。</summary>
    public static double MarginY(bool showHeaders) => showHeaders ? HeaderMarginY : PlainMarginY;

    /// <summary>各列左上角 X 坐标（长度 = Cols）。</summary>
    public static double[] ColumnOffsets(SeatLayout layout, bool showHeaders)
    {
        var xs = new double[layout.Cols];
        if (layout.Cols == 0)
        {
            return xs;
        }

        xs[0] = MarginX(showHeaders);
        for (var col = 1; col < layout.Cols; col++)
        {
            var gap = col - 1 < layout.ColumnGaps.Count ? layout.ColumnGaps[col - 1] : SeatLayout.DefaultGap;
            xs[col] = xs[col - 1] + SeatWidth + gap;
        }

        return xs;
    }

    /// <summary>
    /// 各排左上角 Y 坐标（长度 = Rows）。row 0 为「第一排」，位于最下方、紧邻讲台；
    /// row 越大越靠上，故从顶部（Rows-1）向下累加。
    /// </summary>
    public static double[] RowOffsets(SeatLayout layout, bool showHeaders)
    {
        var ys = new double[layout.Rows];
        if (layout.Rows == 0)
        {
            return ys;
        }

        ys[layout.Rows - 1] = MarginY(showHeaders);
        for (var row = layout.Rows - 2; row >= 0; row--)
        {
            var gap = row < layout.RowGaps.Count ? layout.RowGaps[row] : SeatLayout.DefaultGap;
            ys[row] = ys[row + 1] + SeatHeight + gap;
        }

        return ys;
    }

    /// <summary>网格总宽（不含左右留白）。</summary>
    public static double GridWidth(SeatLayout layout) => layout.Cols * SeatWidth + Sum(layout.ColumnGaps);

    /// <summary>网格总高（不含上下留白）。</summary>
    public static double GridHeight(SeatLayout layout) => layout.Rows * SeatHeight + Sum(layout.RowGaps);

    /// <summary>讲台矩形：白底黑框、位于座位区底部正中，与第一排底边间隔 <see cref="PlatformGap"/>。</summary>
    public static Rect Platform(SeatLayout layout, bool showHeaders)
    {
        var gridWidth = GridWidth(layout);
        var width = Math.Max(200d, gridWidth / 3d);
        var left = MarginX(showHeaders) + gridWidth / 2d - width / 2d;
        var top = MarginY(showHeaders) + GridHeight(layout) + PlatformGap;
        return new Rect(left, top, width, PlatformHeight);
    }

    /// <summary>
    /// 讲台左右两个侧位矩形（可坐人）：水平紧贴讲台左右边，垂直居中于讲台。
    /// 左侧位 X 若为负（列数很少、讲台偏宽）则钳制到 0，避免画到画布外。
    /// </summary>
    public static (Rect Left, Rect Right) PlatformSeats(SeatLayout layout, bool showHeaders)
    {
        var plat = Platform(layout, showHeaders);
        var seatY = plat.Y + (PlatformHeight - SeatHeight) / 2d;
        var leftX = plat.X - SeatWidth;
        if (leftX < 0d)
        {
            leftX = 0d;
        }

        return (
            new Rect(leftX, seatY, SeatWidth, SeatHeight),
            new Rect(plat.X + plat.Width, seatY, SeatWidth, SeatHeight));
    }

    /// <summary>画布尺寸（含留白与底部讲台空间；右侧额外预留一个侧位宽度 + 讲台间隔）。</summary>
    public static Size CanvasSize(SeatLayout layout, bool showHeaders)
    {
        var width = Math.Max(
            MinCanvasWidth,
            2d * MarginX(showHeaders) + GridWidth(layout) + SeatWidth + PlatformGap);
        var height = Math.Max(
            MinCanvasHeight,
            MarginY(showHeaders) + GridHeight(layout) + PlatformGap + PlatformHeight + BottomPadding);
        return new Size(width, height);
    }

    private static double Sum(IReadOnlyList<double> gaps)
    {
        double total = 0d;
        for (var i = 0; i < gaps.Count; i++)
        {
            total += gaps[i];
        }

        return total;
    }
}
