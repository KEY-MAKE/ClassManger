using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>点名引擎单元测试。</summary>
public class RollCallServiceTests
{
    private static readonly IReadOnlyList<Student> Roster = new List<Student>
    {
        new("A", 100),
        new("B", 90),
        new("C", 80),
    };

    [Fact]
    public void LoadRoster_ResetsHistoryAndCalled()
    {
        var service = new RollCallService();
        service.LoadRoster(Roster);

        Assert.Equal(3, service.Students.Count);
        Assert.Empty(service.History);
        Assert.Equal(0, service.State.Called);
        Assert.Equal(3, service.State.Remaining);
        Assert.False(service.IsAllCalled);
    }

    [Fact]
    public void Random_CanRepeat()
    {
        var service = new RollCallService { Mode = RollCallMode.Random };
        service.LoadRoster(Roster);

        for (var i = 0; i < 50; i++)
        {
            var result = service.Roll();
            Assert.NotNull(result);
        }

        // 随机模式允许重复，历史条数等于点名次数
        Assert.Equal(50, service.History.Count);
        Assert.All(service.History, record => Assert.Contains(record.StudentName, new[] { "A", "B", "C" }));

        // 单名学生时，随机模式必然重复点到，且第二次标记 AlreadyCalled=true（真正验证“可重复”语义）
        var single = new RollCallService { Mode = RollCallMode.Random };
        single.LoadRoster(new List<Student> { new("X", 100) });

        var first = single.Roll();
        var second = single.Roll();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("X", first!.Student.Name);
        Assert.Equal("X", second!.Student.Name);
        Assert.False(first.AlreadyCalled);
        Assert.True(second.AlreadyCalled, "随机模式下再次点到同一人应标记 AlreadyCalled=true");
        Assert.Equal(2, single.History.Count);
    }

    [Fact]
    public void Sequential_FollowsOrder_ThenRestarts()
    {
        var service = new RollCallService { Mode = RollCallMode.Sequential };
        service.LoadRoster(Roster);

        Assert.Equal("A", service.Roll()!.Student.Name);
        Assert.Equal("B", service.Roll()!.Student.Name);
        Assert.Equal("C", service.Roll()!.Student.Name);
        Assert.True(service.IsAllCalled);

        // 点完从头开始
        Assert.Equal("A", service.Roll()!.Student.Name);
    }

    [Fact]
    public void UncalledOnly_ReturnsNullWhenAllCalled()
    {
        var service = new RollCallService { Mode = RollCallMode.UncalledOnly };
        service.LoadRoster(Roster);

        var first = service.Roll();
        var second = service.Roll();
        var third = service.Roll();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.True(service.IsAllCalled);

        Assert.Null(service.Roll());
    }

    [Fact]
    public void UncalledOnly_NeverRepeats()
    {
        var service = new RollCallService { Mode = RollCallMode.UncalledOnly };
        service.LoadRoster(Roster);

        var picked = new HashSet<string>(StringComparer.Ordinal);
        while (!service.IsAllCalled)
        {
            var result = service.Roll();
            Assert.NotNull(result);
            Assert.True(picked.Add(result!.Student.Name), "只点未到模式不应重复");
        }
    }

    [Fact]
    public void Reset_ClearsRecordsKeepsRoster()
    {
        var service = new RollCallService { Mode = RollCallMode.Sequential };
        service.LoadRoster(Roster);
        service.Roll();

        service.Reset();

        Assert.Equal(3, service.Students.Count);
        Assert.Empty(service.History);
        Assert.Equal(0, service.State.Called);
    }

    [Fact]
    public void Roll_EmptyRoster_ReturnsNull()
    {
        var service = new RollCallService();
        service.LoadRoster(Array.Empty<Student>());

        Assert.Null(service.Roll());
    }

    [Fact]
    public void State_CompletionPercent_IsOneDecimal()
    {
        var service = new RollCallService { Mode = RollCallMode.Sequential };
        service.LoadRoster(Roster);
        service.Roll();

        // 1/3 = 33.3%
        Assert.Equal(33.3, service.State.CompletionPercent, 1);
    }

    [Fact]
    public void NextSequentialName_PreviewsNext()
    {
        var service = new RollCallService { Mode = RollCallMode.Sequential };
        service.LoadRoster(Roster);

        Assert.Equal("A", service.NextSequentialName());
        service.Roll();
        Assert.Equal("B", service.NextSequentialName());
    }
}
