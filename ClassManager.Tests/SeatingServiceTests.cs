using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>排座服务单元测试。</summary>
public class SeatingServiceTests
{
    private static List<Student> Roster(int count)
    {
        var list = new List<Student>();
        for (var i = 0; i < count; i++)
        {
            list.Add(new Student($"S{i:00}", 100 - i));
        }

        return list;
    }

    [Fact]
    public void Snake_FillsEvenRowsLeftToRight_OddRowsRightToLeft()
    {
        var service = new SeatingService();
        var roster = new List<Student> { new("A", 5), new("B", 4), new("C", 3), new("D", 2), new("E", 1) };
        service.LoadRoster(roster);
        service.UpdateLayout(3, 4);

        service.ArrangeAuto(SeatAlgorithm.Snake);

        // 第 0 行 左→右
        Assert.Equal("A", service.GetStudentAt(0, 0));
        Assert.Equal("B", service.GetStudentAt(0, 1));
        Assert.Equal("C", service.GetStudentAt(0, 2));
        Assert.Equal("D", service.GetStudentAt(0, 3));
        // 第 1 行 右→左
        Assert.Equal("E", service.GetStudentAt(1, 3));
    }

    [Fact]
    public void Grouped_UsesInterleavedOrder()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(9));
        service.UpdateLayout(3, 4);

        service.ArrangeAuto(SeatAlgorithm.Grouped);

        // 分组模式占用全部 9 名学生
        Assert.Equal(9, service.Layout.OccupiedSeats);
        var seatedNames = service.Layout.Seats.Values.ToHashSet();
        Assert.Equal(9, seatedNames.Count);

        // 真正验证「好 / 中 / 差」三组交错：Roster(9) 成绩 100..92，groupSize=3，
        // 成绩区间：好=[98,100]、中=[95,97]、差=[92,94]。
        // 行优先落座后，位置依次应为 好,中,差,好,中,差,好,中,差。
        var scoreOf = service.Students.ToDictionary(s => s.Name, s => s.Score);
        double ScoreAt(int row, int col) => scoreOf[service.GetStudentAt(row, col)!];

        Assert.InRange(ScoreAt(0, 0), 98, 100); // 好
        Assert.InRange(ScoreAt(0, 1), 95, 97);  // 中
        Assert.InRange(ScoreAt(0, 2), 92, 94);  // 差
        Assert.InRange(ScoreAt(0, 3), 98, 100); // 好（第 2 轮）
        Assert.InRange(ScoreAt(1, 0), 95, 97);  // 中
        Assert.InRange(ScoreAt(1, 1), 92, 94);  // 差
        Assert.InRange(ScoreAt(1, 2), 98, 100); // 好（第 3 轮）
        Assert.InRange(ScoreAt(1, 3), 95, 97);  // 中
        Assert.InRange(ScoreAt(2, 0), 92, 94);  // 差
    }

    [Fact]
    public void Random_PlacesAllStudents()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(10));
        service.UpdateLayout(4, 5);

        service.ArrangeAuto(SeatAlgorithm.Random);

        Assert.Equal(10, service.Layout.OccupiedSeats);

        // 唯一落座：10 个座位对应 10 名不同学生，且集合等于名单集合（无重复、无遗漏）
        var seatedNames = service.Layout.Seats.Values.ToHashSet();
        Assert.Equal(10, seatedNames.Count);
        Assert.Equal(
            service.Students.Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            seatedNames.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void UpdateLayout_KeepsSeatsWithinBounds_DropsOthers()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(20));
        service.UpdateLayout(6, 8);

        service.Layout.Set(0, 0, "Keep1");
        service.Layout.Set(4, 6, "Keep2");
        service.Layout.Set(7, 2, "Drop1");
        service.Layout.Set(2, 9, "Drop2");

        service.UpdateLayout(5, 7);

        Assert.Equal("Keep1", service.GetStudentAt(0, 0));
        Assert.Equal("Keep2", service.GetStudentAt(4, 6));
        Assert.Equal(2, service.Layout.OccupiedSeats);
    }

    [Fact]
    public void UpdateLayout_InvalidRange_Throws()
    {
        var service = new SeatingService();

        // 行数边界：合法 [1,20]
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(0, 8));  // 行 < 1
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(-1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(21, 8)); // 行 > 20
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(100, 8));

        // 列数边界：合法 [1,20]
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(6, 0));  // 列 < 1
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(6, -2));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(6, 21)); // 列 > 20
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLayout(6, 99));

        // 边界内合法值不得抛异常
        service.UpdateLayout(1, 1);
        service.UpdateLayout(20, 20);
    }

    [Fact]
    public void TryPlace_EmptySeat_Places()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(3));
        service.UpdateLayout(3, 4);

        var placed = service.TryPlace(service.Students[0], 1, 1, out var move);

        Assert.True(placed);
        Assert.Null(move);
        Assert.Equal("S00", service.GetStudentAt(1, 1));
    }

    [Fact]
    public void TryPlace_StudentAlreadySeated_ReturnsMoveInfo()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(3));
        service.UpdateLayout(3, 4);
        service.TryPlace(service.Students[0], 0, 0, out _);

        var placed = service.TryPlace(service.Students[0], 2, 2, out var move);

        Assert.False(placed);
        Assert.NotNull(move);
        Assert.Equal((0, 0), move!.From);
    }

    [Fact]
    public void MoveTo_RemovesFromOldSeat()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(3));
        service.UpdateLayout(3, 4);
        service.TryPlace(service.Students[0], 0, 0, out _);

        service.MoveTo(service.Students[0], 2, 3);

        Assert.Null(service.GetStudentAt(0, 0));
        Assert.Equal("S00", service.GetStudentAt(2, 3));
    }

    [Fact]
    public void ClearSeat_EmptiesSeat()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(3));
        service.UpdateLayout(3, 4);
        service.TryPlace(service.Students[0], 1, 1, out _);

        service.ClearSeat(1, 1);

        Assert.Null(service.GetStudentAt(1, 1));
        Assert.Equal(0, service.Layout.OccupiedSeats);
        Assert.Equal(service.Layout.TotalSeats, service.Layout.EmptySeats);
    }

    [Fact]
    public void LoadRoster_ClearsSeats()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(3));
        service.UpdateLayout(3, 4);
        service.ArrangeAuto(SeatAlgorithm.Snake);
        Assert.True(service.Layout.OccupiedSeats > 0);

        service.LoadRoster(Roster(5));

        Assert.Equal(0, service.Layout.OccupiedSeats);
    }

    [Fact]
    public void IsSeated_ReflectsPlacement()
    {
        var service = new SeatingService();
        service.LoadRoster(Roster(3));
        service.UpdateLayout(3, 4);

        Assert.False(service.IsSeated("S00"));
        service.TryPlace(service.Students[0], 0, 0, out _);
        Assert.True(service.IsSeated("S00"));
    }
}
