using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 后台下载管理器的契约。
/// <para>
/// <b>真实反馈</b>：关掉「关于」窗口后下载就停了，最后报失败。
/// 根因是下载进度用的 <c>Progress&lt;int&gt;</c> 会把回调投递到 UI 线程的
/// SynchronizationContext —— 窗口一关 Dispatcher 没了，<c>Report()</c> 抛异常，
/// 把下载整个打断。
/// </para>
/// <para>
/// 所以下载要独立于窗口生命周期，且任何订阅方（包括已关闭窗口）出问题都不能影响下载。
/// </para>
/// </summary>
public class UpdateDownloadManagerTests
{
    private static UpdateManifest MakeManifest() => new()
    {
        Version = "9.99",
        Url = "https://example.invalid/app.exe",
        SizeBytes = 1024,
        Sha256 = "x",
        Notes = "test",
    };

    /// <summary>开始下载后状态变 Downloading，进度能收到。</summary>
    [Fact]
    public async Task Starts_and_reports_progress()
    {
        var fake = new FakeUpdateService();
        using var mgr = new UpdateDownloadManager(fake);

        var sawProgress = new TaskCompletionSource();
        mgr.Changed += (_, _) =>
        {
            if (mgr.IsDownloading && mgr.Percent > 0)
            {
                sawProgress.TrySetResult();
            }
        };

        mgr.Start(MakeManifest());

        Assert.Equal(UpdateDownloadManager.DownloadState.Downloading, mgr.State);

        var completed = await Task.WhenAny(sawProgress.Task, Task.Delay(5000));
        Assert.Same(sawProgress.Task, completed);
    }

    /// <summary>
    /// 核心回归：订阅方抛异常（等价于「窗口已关闭，UI 上下文没了」）
    /// 绝不能打断下载。
    /// </summary>
    [Fact]
    public async Task Subscriber_exception_does_not_break_download()
    {
        var fake = new FakeUpdateService();
        using var mgr = new UpdateDownloadManager(fake);

        var finished = new TaskCompletionSource();

        // 第一个订阅者每次都被调用就抛 —— 模拟已销毁的窗口
        mgr.Changed += (_, _) => throw new InvalidOperationException("窗口已关闭");
        mgr.Changed += (_, _) =>
        {
            if (mgr.State == UpdateDownloadManager.DownloadState.Completed)
            {
                finished.TrySetResult();
            }
        };

        mgr.Start(MakeManifest());

        var done = await Task.WhenAny(finished.Task, Task.Delay(8000));

        Assert.Same(finished.Task, done);
        Assert.Equal(UpdateDownloadManager.DownloadState.Completed, mgr.State);
        Assert.Equal(100, mgr.Percent);
    }

    /// <summary>坏订阅者不能把后面的订阅者也弄瞎。</summary>
    [Fact]
    public async Task Bad_subscriber_does_not_silence_others()
    {
        var fake = new FakeUpdateService();
        using var mgr = new UpdateDownloadManager(fake);

        var secondSawProgress = new TaskCompletionSource();

        mgr.Changed += (_, _) => throw new Exception("坏的订阅者");
        mgr.Changed += (_, _) =>
        {
            if (mgr.Percent > 0)
            {
                secondSawProgress.TrySetResult();
            }
        };

        mgr.Start(MakeManifest());

        var done = await Task.WhenAny(secondSawProgress.Task, Task.Delay(5000));
        Assert.Same(secondSawProgress.Task, done);
    }

    /// <summary>下载失败时状态是 Failed 并带错误说明。</summary>
    [Fact]
    public async Task Failure_is_reported()
    {
        var fake = new FakeUpdateService { FailWith = "网络断了" };
        using var mgr = new UpdateDownloadManager(fake);

        var failed = new TaskCompletionSource();
        mgr.Changed += (_, _) =>
        {
            if (mgr.State == UpdateDownloadManager.DownloadState.Failed)
            {
                failed.TrySetResult();
            }
        };

        mgr.Start(MakeManifest());

        var done = await Task.WhenAny(failed.Task, Task.Delay(8000));
        Assert.Same(failed.Task, done);
        Assert.Contains("网络断了", mgr.Message);
    }

    /// <summary>已经在下载时再 Start 不会重复启动（窗口反复开关的场景）。</summary>
    [Fact]
    public async Task Start_is_idempotent_while_downloading()
    {
        var fake = new FakeUpdateService { HoldMs = 1500 };
        using var mgr = new UpdateDownloadManager(fake);

        mgr.Start(MakeManifest());
        var countAfterFirst = fake.DownloadCalls;

        mgr.Start(MakeManifest());
        mgr.Start(MakeManifest());

        Assert.Equal(countAfterFirst, fake.DownloadCalls);

        await Task.Delay(2200);
        Assert.Equal(1, fake.DownloadCalls);
    }

    /// <summary>完成后会启动新包（由新包覆盖安装）。</summary>
    [Fact]
    public async Task Launches_installer_after_download()
    {
        var fake = new FakeUpdateService();
        using var mgr = new UpdateDownloadManager(fake);

        var finished = new TaskCompletionSource();
        mgr.Changed += (_, _) =>
        {
            if (mgr.State == UpdateDownloadManager.DownloadState.Completed)
            {
                finished.TrySetResult();
            }
        };

        mgr.Start(MakeManifest());

        await Task.WhenAny(finished.Task, Task.Delay(8000));
        await Task.Delay(1500);   // 管理器内部有 700ms 的「显示完成」延迟

        Assert.Single(fake.Launched);
    }

    /// <summary>Reset 只在非下载态生效。</summary>
    [Fact]
    public async Task Reset_clears_finished_state()
    {
        var fake = new FakeUpdateService { FailWith = "x" };
        using var mgr = new UpdateDownloadManager(fake);

        mgr.Start(MakeManifest());
        await Task.Delay(1500);

        Assert.Equal(UpdateDownloadManager.DownloadState.Failed, mgr.State);

        mgr.Reset();

        Assert.Equal(UpdateDownloadManager.DownloadState.Idle, mgr.State);
        Assert.Equal(0, mgr.Percent);
    }

    /// <summary>假更新服务：可控制耗时、失败、以及记录 Launch 调用。</summary>
    private sealed class FakeUpdateService : IUpdateService
    {
        public int DownloadCalls { get; private set; }

        public string? FailWith { get; init; }

        public int HoldMs { get; init; } = 200;

        public List<string> Launched { get; } = new();

        public UpdateSettings Settings { get; } = new() { SourceUrl = "http://test/version.json" };

        public string DownloadsDirectory => Path.GetTempPath();

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(UpdateCheckResult.Latest("test"));

        public async Task<UpdateDownloadResult> DownloadAsync(
            UpdateManifest info,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;

            for (var p = 10; p <= 100; p += 30)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(Math.Min(p, 100));
                await Task.Delay(Math.Max(20, HoldMs / 4), cancellationToken);
            }

            if (FailWith is not null)
            {
                return UpdateDownloadResult.Fail(FailWith);
            }

            var path = Path.Combine(Path.GetTempPath(), "cm_fake_update.exe");
            File.WriteAllText(path, "fake");

            return UpdateDownloadResult.Success(path);
        }

        public bool Launch(string filePath)
        {
            Launched.Add(filePath);
            return true;
        }

        public bool OpenDownloadPage(UpdateManifest info) => true;

        public UpdateDownloadTarget ResolveDownloadTarget(UpdateManifest info) =>
            new() { IsWeb = true, Path = info.Url };

        public void Save() { }
    }
}
