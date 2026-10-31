using System;
using System.IO;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>座次表 xlsx 导出→导入 的往返一致性（格式契约回归测试）。</summary>
public class SeatSheetRoundTripTests
{
    [Fact]
    public void RoundTrip_PreservesLayoutAndSeats()
    {
        var svc = new ExportService();

        var original = new SeatLayout();
        original.Resize(5, 7);
        original.ShowHeaders = false;

        // 制造非默认间距（逐道不同）
        for (var i = 0; i < original.ColumnGaps.Count; i++)
        {
            original.SetColumnGap(i, 12 + i * 9);
        }

        for (var i = 0; i < original.RowGaps.Count; i++)
        {
            original.SetRowGap(i, 20 + i * 5);
        }

        original.Set(0, 0, "张三");
        original.Set(0, 6, "李四");
        original.Set(4, 3, "王五");
        original.Set(2, 2, "赵六");

        // 讲台两侧的额外座位（伪坐标 row = -1），验证也能往返
        original.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol, "讲台甲");
        original.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol, "讲台乙");

        var path = Path.Combine(Path.GetTempPath(), $"cm_roundtrip_{Guid.NewGuid():N}.xlsx");
        svc.ExportSeatXlsx(path, original, "高二(3)班", DateTime.Now);

        var restored = new SeatLayout();
        var ok = SeatSheetImporter.TryImport(path, restored, out var message);

        Console.WriteLine($"ROUNDTRIP_OK={ok} MSG={message}");
        Console.WriteLine($"ORIG rows={original.Rows} cols={original.Cols} headers={original.ShowHeaders} occupied={original.OccupiedSeats}");
        Console.WriteLine($"BACK rows={restored.Rows} cols={restored.Cols} headers={restored.ShowHeaders} occupied={restored.OccupiedSeats}");

        Assert.True(ok, message);
        Assert.Equal(original.Rows, restored.Rows);
        Assert.Equal(original.Cols, restored.Cols);
        Assert.Equal(original.ShowHeaders, restored.ShowHeaders);
        Assert.Equal(original.OccupiedSeats, restored.OccupiedSeats);

        for (var i = 0; i < original.ColumnGaps.Count; i++)
        {
            Assert.Equal(original.ColumnGaps[i], restored.ColumnGaps[i], 3);
        }

        for (var i = 0; i < original.RowGaps.Count; i++)
        {
            Assert.Equal(original.RowGaps[i], restored.RowGaps[i], 3);
        }

        Assert.Equal("张三", restored.Get(0, 0));
        Assert.Equal("李四", restored.Get(0, 6));
        Assert.Equal("王五", restored.Get(4, 3));
        Assert.Equal("赵六", restored.Get(2, 2));
        Assert.Null(restored.Get(1, 1));

        // 讲台两侧座位必须一并还原（之前漏掉了）
        Assert.Equal("讲台甲", restored.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol));
        Assert.Equal("讲台乙", restored.Get(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol));

        // 再验一次拒绝非法文件
        var bad = Path.Combine(Path.GetTempPath(), $"cm_bad_{Guid.NewGuid():N}.xlsx");
        MiniExcelLibs.MiniExcel.SaveAs(bad, new[] { new { A = 1, B = "x" } }, printHeader: true);
        var reject = new SeatLayout();
        var ok2 = SeatSheetImporter.TryImport(bad, reject, out var message2);
        Console.WriteLine($"REJECT_OK={!ok2} MSG={message2}");
        Assert.False(ok2);

        File.Delete(path);
        File.Delete(bad);
    }
}
