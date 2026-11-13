using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 「等目标文件释放」的契约。
/// <para>
/// 真实场景：应用内点「立即更新」时，时序是 <c>Launch(新exe)</c> → <c>Shutdown()</c>，
/// 两个进程并发。新进程往往比旧进程退得早 —— 它去覆盖
/// <c>ClassManager.App.exe</c> 时，旧进程还占着这个文件，覆盖就会失败。
/// 所以要等，而不是立刻报错。
/// </para>
/// </summary>
public class InstallFileLockTests : IDisposable
{
    private readonly string _dir;

    public InstallFileLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cm_lock_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    private static bool WaitUnlocked(string path, TimeSpan budget)
    {
        var m = typeof(InstallService).GetMethod(
            "WaitUntilFileUnlocked", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(m);

        return (bool)m!.Invoke(null, new object[] { path, budget })!;
    }

    /// <summary>文件不存在 → 立刻返回 true（没什么可等的）。</summary>
    [Fact]
    public void Missing_file_is_immediately_ready()
    {
        var sw = Stopwatch.StartNew();
        Assert.True(WaitUnlocked(Path.Combine(_dir, "nope.exe"), TimeSpan.FromSeconds(5)));
        Assert.True(sw.ElapsedMilliseconds < 500, "不该为一个不存在的文件等待");
    }

    /// <summary>没人占用 → 立刻返回 true。</summary>
    [Fact]
    public void Free_file_is_immediately_ready()
    {
        var path = Path.Combine(_dir, "free.exe");
        File.WriteAllText(path, "x");

        var sw = Stopwatch.StartNew();
        Assert.True(WaitUnlocked(path, TimeSpan.FromSeconds(5)));
        Assert.True(sw.ElapsedMilliseconds < 500);
    }

    /// <summary>
    /// 核心：文件被占用 2 秒后释放 → 应该等到它释放并返回 true，
    /// 而不是超时失败。这正是「旧进程正在退出」的情形。
    /// </summary>
    [Fact]
    public void Waits_for_lock_to_be_released()
    {
        var path = Path.Combine(_dir, "locked.exe");
        File.WriteAllText(path, "x");

        // 后台占住 2 秒
        var holder = new Thread(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Thread.Sleep(2000);
        });

        holder.Start();
        Thread.Sleep(200);   // 确保它先拿到锁

        var sw = Stopwatch.StartNew();
        var ok = WaitUnlocked(path, TimeSpan.FromSeconds(15));
        sw.Stop();

        holder.Join(TimeSpan.FromSeconds(10));

        Assert.True(ok, "占用释放后应该返回 true，而不是超时失败");
        Assert.True(sw.ElapsedMilliseconds >= 1500, $"应该真的等过（实际 {sw.ElapsedMilliseconds}ms）");
        Assert.True(sw.ElapsedMilliseconds < 10000, $"不该等太久（实际 {sw.ElapsedMilliseconds}ms）");
    }

    /// <summary>一直被占用 → 超时后返回 false（调用方会记日志但不硬失败）。</summary>
    [Fact]
    public void Returns_false_when_still_locked_after_budget()
    {
        var path = Path.Combine(_dir, "stuck.exe");
        File.WriteAllText(path, "x");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var sw = Stopwatch.StartNew();
        var ok = WaitUnlocked(path, TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.False(ok);
        Assert.True(sw.ElapsedMilliseconds >= 1500, $"应该等满预算（实际 {sw.ElapsedMilliseconds}ms）");
        Assert.True(sw.ElapsedMilliseconds < 8000, $"不该超出预算太多（实际 {sw.ElapsedMilliseconds}ms）");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // 忽略
        }
    }
}
