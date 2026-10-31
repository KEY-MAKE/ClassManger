using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>座位布局（SeatLayout）的间隙模型与几何切分单元测试。</summary>
public class SeatLayoutTests
{
    [Fact]
    public void DefaultLayout_HasEightPixelGaps_AndHeadersOn()
    {
        var layout = new SeatLayout();

        Assert.Equal(6, layout.Rows);
        Assert.Equal(8, layout.Cols);
        Assert.Equal(7, layout.ColumnGaps.Count); // Cols - 1
        Assert.Equal(5, layout.RowGaps.Count);    // Rows - 1
        Assert.All(layout.ColumnGaps, g => Assert.Equal(8d, g));
        Assert.All(layout.RowGaps, g => Assert.Equal(8d, g));
        Assert.True(layout.ShowHeaders);
    }

    [Fact]
    public void Resize_PreservesExistingGaps_FillsNewWithDefault()
    {
        var layout = new SeatLayout();
        layout.SetColumnGap(1, 50d);
        layout.SetRowGap(0, 30d);

        // 扩展列/行：已有间隙保留，新增填默认 8
        layout.Resize(6, 10);
        Assert.Equal(9, layout.ColumnGaps.Count);
        Assert.Equal(50d, layout.ColumnGaps[1]);
        Assert.Equal(8d, layout.ColumnGaps[8]);
        Assert.Equal(30d, layout.RowGaps[0]);

        // 收缩：越界间隙被丢弃
        layout.Resize(3, 3);
        Assert.Equal(2, layout.ColumnGaps.Count);
        Assert.Equal(2, layout.RowGaps.Count);
        Assert.Equal(50d, layout.ColumnGaps[1]);
    }

    [Fact]
    public void SetGap_NormalizesInvalidValues()
    {
        var layout = new SeatLayout();

        layout.SetColumnGap(0, -5d);
        Assert.Equal(8d, layout.ColumnGaps[0]); // 负值 → 默认 8

        layout.SetColumnGap(0, double.NaN);
        Assert.Equal(8d, layout.ColumnGaps[0]); // NaN → 默认 8

        layout.SetColumnGap(0, 999d);
        Assert.Equal(400d, layout.ColumnGaps[0]); // 上界夹到 400

        layout.SetRowGap(0, 123d);
        Assert.Equal(123d, layout.RowGaps[0]);
    }

    [Fact]
    public void SetAllGaps_AppliesToEveryGap()
    {
        var layout = new SeatLayout();

        layout.SetAllColumnGaps(40d);
        layout.SetAllRowGaps(-1d);

        Assert.All(layout.ColumnGaps, g => Assert.Equal(40d, g));
        Assert.All(layout.RowGaps, g => Assert.Equal(8d, g)); // -1 归一化为默认 8
    }

    /// <summary>每一列各有一个 x 偏移（即列表头「第x列」逐列对齐），宽间隙只推移其后的列。</summary>
    [Fact]
    public void ColumnOffsets_OnePerColumn_WideGapShiftsFollowingColumns()
    {
        var layout = new SeatLayout();
        layout.Resize(6, 8);

        var offsets = SeatMapGeometry.ColumnOffsets(layout, true);
        Assert.Equal(8, offsets.Length);

        // 默认间隙下相邻列等距
        for (var i = 1; i < offsets.Length; i++)
        {
            Assert.Equal(SeatMapGeometry.SeatWidth + SeatLayout.DefaultGap, offsets[i] - offsets[i - 1], 3);
        }

        // 在第 4 列之后设一道 40px 宽间隙 → 第 5 列及之后整体右移 32，第 1~4 列不动
        layout.SetColumnGap(3, 40d);
        var shifted = SeatMapGeometry.ColumnOffsets(layout, true);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(offsets[i], shifted[i], 3);
        }

        for (var i = 4; i < shifted.Length; i++)
        {
            Assert.Equal(offsets[i] + 32d, shifted[i], 3);
        }
    }

    [Fact]
    public void Geometry_RowZero_IsAtBottom_NearestPlatform()
    {
        var layout = new SeatLayout();

        var ys = SeatMapGeometry.RowOffsets(layout, showHeaders: true);
        // row 0（第一排）应位于最下方 → y 最大；row Rows-1 在最上方 → y 最小
        Assert.True(ys[0] > ys[layout.Rows - 1]);
        Assert.Equal(SeatMapGeometry.MarginY(true), ys[layout.Rows - 1]);

        // 讲台位于网格正下方
        var platform = SeatMapGeometry.Platform(layout, showHeaders: true);
        var gridBottom = SeatMapGeometry.MarginY(true) + SeatMapGeometry.GridHeight(layout);
        Assert.Equal(gridBottom + SeatMapGeometry.PlatformGap, platform.Y);
    }
}
