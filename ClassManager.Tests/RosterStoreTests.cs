using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 上次名单快照的契约。
/// <para>
/// 需求：每次启动都要重新选成绩单太麻烦，启动时应该直接恢复上次的名单能点名。
/// </para>
/// <para>
/// 注意：<see cref="RosterStore"/> 的路径是固定的 %AppData% 位置，
/// 所以这里的每个用例都会先备份用户现有快照，结束后原样恢复。
/// </para>
/// </summary>
public class RosterStoreTests : IDisposable
{
    private readonly string? _backup;
    private readonly bool _existed;

    public RosterStoreTests()
    {
        _existed = File.Exists(RosterStore.FilePath);
        _backup = _existed ? File.ReadAllText(RosterStore.FilePath) : null;

        RosterStore.Clear();
    }

    private static ImportResult MakeResult(string className, params (string Name, double Score)[] data) =>
        new(
            data.Select(d => new Student(d.Name, d.Score)).ToList(),
            "C:/fake/成绩单.xlsx",
            className,
            "姓名",
            "成绩",
            false,
            false,
            null);

    /// <summary>存进去能原样读回来。</summary>
    [Fact]
    public void Save_then_load_round_trips()
    {
        RosterStore.Save(MakeResult("高一(3)班", ("张三", 95), ("李四", 88)));

        var loaded = RosterStore.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal("高一(3)班", loaded!.ClassName);
        Assert.Equal(2, loaded.Students.Count);
        Assert.Equal("张三", loaded.Students[0].Name);
        Assert.Equal(95, loaded.Students[0].Score);
        Assert.Equal("李四", loaded.Students[1].Name);
    }

    /// <summary>没有快照时返回 null（首次启动的正常情况）。</summary>
    [Fact]
    public void No_snapshot_returns_null()
    {
        Assert.Null(RosterStore.TryLoad());
    }

    /// <summary>文件损坏时返回 null，而不是让启动崩掉。</summary>
    [Fact]
    public void Corrupted_file_returns_null()
    {
        var dir = Path.GetDirectoryName(RosterStore.FilePath);

        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(RosterStore.FilePath, "{ 这不是合法 json ");

        Assert.Null(RosterStore.TryLoad());
    }

    /// <summary>空名单不算有效快照。</summary>
    [Fact]
    public void Empty_roster_is_ignored()
    {
        RosterStore.Save(MakeResult("空班"));

        Assert.Null(RosterStore.TryLoad());
    }

    /// <summary>Clear 之后读不到。</summary>
    [Fact]
    public void Clear_removes_snapshot()
    {
        RosterStore.Save(MakeResult("高一(3)班", ("张三", 95)));

        Assert.NotNull(RosterStore.TryLoad());

        RosterStore.Clear();

        Assert.Null(RosterStore.TryLoad());
    }

    /// <summary>再存一次会覆盖旧的（换了班级导入新表）。</summary>
    [Fact]
    public void Save_overwrites_previous()
    {
        RosterStore.Save(MakeResult("旧班", ("旧同学", 60)));
        RosterStore.Save(MakeResult("新班", ("新同学", 90), ("新同学2", 80)));

        var loaded = RosterStore.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal("新班", loaded!.ClassName);
        Assert.Equal(2, loaded.Students.Count);
        Assert.DoesNotContain(loaded.Students, s => s.Name == "旧同学");
    }

    /// <summary>姓名为空白的行会被剔除。</summary>
    [Fact]
    public void Blank_names_are_dropped()
    {
        RosterStore.Save(MakeResult("高一(3)班", ("张三", 95), ("   ", 88), ("", 77)));

        var loaded = RosterStore.TryLoad();

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Students);
        Assert.Equal("张三", loaded.Students[0].Name);
    }

    public void Dispose()
    {
        RosterStore.Clear();

        if (_existed && _backup is not null)
        {
            try
            {
                var dir = Path.GetDirectoryName(RosterStore.FilePath);

                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(RosterStore.FilePath, _backup);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
