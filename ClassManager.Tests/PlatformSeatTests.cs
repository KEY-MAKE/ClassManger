using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>讲台左右两个可坐人侧位（伪行 row = -1）的模型、排座、统计与几何单元测试。</summary>
public class PlatformSeatTests
{
    private static List<Student> Roster(int count)
        => Enumerable.Range(1, count).Select(i => new Student($"学生{i:00}", 100 - i)).ToList();

    [Fact]
    public void PlatformSeat_Constants_UsePseudoRowMinusOne()
    {
        Assert.Equal(-1, SeatLayout.PlatformSeatRow);
        Assert.Equal(0, SeatLayout.PlatformSeatLeftCol);
        Assert.Equal(1, SeatLayout.PlatformSeatRightCol);
        Assert.Equal(2, SeatLayout.PlatformSeatCount);
        Assert.True(SeatLayout.IsPlatformSeat(SeatLayout.PlatformSeatRow));
        Assert.False(SeatLayout.IsPlatformSeat(0));
    }

    [Fact]
    public void PlatformSeat_GetSetClear_RoundTrips()
    {
        var layout = new SeatLayout();

        Assert.Null(layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol));

        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol, "甲");
        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol, "乙");

        Assert.Equal("甲", layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol));
        Assert.Equal("乙", layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol));
        Assert.Equal(2, layout.OccupiedSeats);

        layout.Clear(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol);
        Assert.Null(layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol));
        Assert.Equal("乙", layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol));
        Assert.Equal(1, layout.OccupiedSeats);
    }

    [Fact]
    public void TotalSeats_IncludesPlatformSeats()
    {
        var layout = new SeatLayout();
        layout.Resize(4, 6);

        Assert.Equal(4 * 6 + SeatLayout.PlatformSeatCount, layout.TotalSeats);
    }

    [Fact]
    public void Resize_KeepsPlatformSeats_AndDropsOutOfRangeGridSeats()
    {
        var layout = new SeatLayout();
        layout.Resize(6, 8);
        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol, "讲台左同学");
        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol, "讲台右同学");
        layout.Set(5, 7, "网格原座");   // 改小后会越界被丢弃
        layout.Set(0, 0, "网格保留座");

        layout.Resize(4, 6);

        // 讲台侧位不参与网格越界判定，必须原样保留
        Assert.Equal("讲台左同学", layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol));
        Assert.Equal("讲台右同学", layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol));
        Assert.Equal("网格保留座", layout.Get(0, 0));
        Assert.Null(layout.Get(5, 7));
        Assert.Equal(3, layout.OccupiedSeats);
    }

    [Fact]
    public void TryFindSeat_LocatesStudentOnPlatformSeat()
    {
        var layout = new SeatLayout();
        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol, "讲台右同学");

        Assert.True(layout.TryFindSeat("讲台右同学", out var seat));
        Assert.Equal(SeatLayout.PlatformSeatRow, seat.Row);
        Assert.Equal(SeatLayout.PlatformSeatRightCol, seat.Col);
    }

    [Theory]
    [InlineData(SeatAlgorithm.Snake)]
    [InlineData(SeatAlgorithm.Grouped)]
    [InlineData(SeatAlgorithm.Random)]
    public void ArrangeAuto_GridFull_OverflowGoesToPlatformSeats_LeftFirst(SeatAlgorithm algorithm)
    {
        var service = new SeatingService();
        service.UpdateLayout(2, 3);                 // 网格 6 个座位
        service.LoadRoster(Roster(8));              // 8 人 → 溢出 2 人

        service.ArrangeAuto(algorithm);

        Assert.Equal(8, service.Layout.OccupiedSeats);
        var left = service.Layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol);
        var right = service.Layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol);
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.NotEqual(left, right);

        // 网格 6 个座位全部有人，侧位恰好 2 人
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                Assert.NotNull(service.Layout.Get(row, col));
            }
        }
    }

    [Theory]
    [InlineData(SeatAlgorithm.Snake)]
    [InlineData(SeatAlgorithm.Grouped)]
    [InlineData(SeatAlgorithm.Random)]
    public void ArrangeAuto_GridNotFull_LeavesPlatformSeatsEmpty(SeatAlgorithm algorithm)
    {
        var service = new SeatingService();
        service.UpdateLayout(3, 4);      // 12 个网格座位
        service.LoadRoster(Roster(4));   // 仅 4 人

        service.ArrangeAuto(algorithm);

        Assert.Null(service.Layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol));
        Assert.Null(service.Layout.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol));
        Assert.Equal(4, service.Layout.OccupiedSeats);
    }

    [Fact]
    public void OccupiedAndEmptySeats_CountPlatformSeats()
    {
        var layout = new SeatLayout();
        layout.Resize(3, 4);            // 12 网格 + 2 侧位 = 14
        Assert.Equal(14, layout.TotalSeats);
        Assert.Equal(0, layout.OccupiedSeats);
        Assert.Equal(14, layout.EmptySeats);

        layout.Set(0, 0, "甲");
        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol, "乙");

        Assert.Equal(2, layout.OccupiedSeats);
        Assert.Equal(12, layout.EmptySeats);
    }

    [Fact]
    public void ClearAll_AlsoClearsPlatformSeats()
    {
        var layout = new SeatLayout();
        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol, "甲");
        layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol, "乙");

        layout.ClearAll();

        Assert.Equal(0, layout.OccupiedSeats);
    }

    [Fact]
    public void PlatformSeats_SitFlushAgainstPlatform_CenteredVertically()
    {
        var layout = new SeatLayout();
        layout.Resize(4, 6);

        var plat = SeatMapGeometry.Platform(layout, layout.ShowHeaders);
        var (left, right) = SeatMapGeometry.PlatformSeats(layout, layout.ShowHeaders);

        // 水平紧贴讲台左右边
        Assert.Equal(plat.X - SeatMapGeometry.SeatWidth, left.X, 3);
        Assert.Equal(plat.X + plat.Width, right.X, 3);

        // 垂直居中于讲台
        Assert.Equal(plat.Y + (SeatMapGeometry.PlatformHeight - SeatMapGeometry.SeatHeight) / 2d, left.Y, 3);
        Assert.Equal(left.Y, right.Y, 3);
    }

    [Fact]
    public void PlatformSeats_LeftIsClampedInsideCanvas_WhenNarrow()
    {
        var layout = new SeatLayout();
        layout.Resize(1, 1);   // 极窄布局，讲台比网格宽

        var (left, right) = SeatMapGeometry.PlatformSeats(layout, showHeaders: false);
        var canvas = SeatMapGeometry.CanvasSize(layout, showHeaders: false);

        Assert.True(left.X >= 0d, "左侧位不得画到画布外");
        Assert.True(left.X + left.Width <= canvas.Width);
        Assert.True(right.X + right.Width <= canvas.Width, "右侧位不得超出画布宽度");
    }

    [Fact]
    public void CanvasSize_ReservesRoomForPlatformSideSeats()
    {
        var layout = new SeatLayout();
        layout.Resize(20, 20);
        layout.SetAllColumnGaps(SeatLayout.MaxGap);   // 极端间隙
        layout.SetAllRowGaps(SeatLayout.MaxGap);

        var canvas = SeatMapGeometry.CanvasSize(layout, showHeaders: true);
        var (left, right) = SeatMapGeometry.PlatformSeats(layout, showHeaders: true);

        Assert.True(left.X >= 0d);
        Assert.True(right.X + right.Width <= canvas.Width + 0.001,
            $"右侧位右边界 {right.X + right.Width} 不应超出画布宽度 {canvas.Width}");
        Assert.True(left.X + left.Width <= canvas.Width + 0.001);
    }
}
