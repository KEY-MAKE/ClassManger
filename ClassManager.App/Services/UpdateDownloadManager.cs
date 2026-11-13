using System.Windows;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>
/// 更新下载的后台管理者（应用级单例）。
/// <para>
/// 为什么需要它：原来的下载跑在「关于」窗口的 ViewModel 里，进度用的是
/// <c>new Progress&lt;int&gt;(...)</c> —— 它会把回调投递到 UI 线程的 SynchronizationContext。
/// 用户一关窗口，Dispatcher 没了，<c>Report()</c> 直接抛异常，把下载打断，
/// 表现为「关掉窗口 → 下载停止 → 最后报失败」。
/// </para>
/// <para>
/// 所以下载必须脱离窗口生命周期：用同步进度回调，状态存在这里，
/// 窗口关掉继续跑，下次打开还能看到进度。
/// </para>
/// </summary>
public sealed class UpdateDownloadManager : IDisposable
{
    /// <summary>下载状态。</summary>
    public enum DownloadState
    {
        /// <summary>没有进行中的下载。</summary>
        Idle,

        /// <summary>正在下载。</summary>
        Downloading,

        /// <summary>下载完成（已开始安装）。</summary>
        Completed,

        /// <summary>下载失败。</summary>
        Failed,
    }

    private readonly IUpdateService _update;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _task;

    public UpdateDownloadManager(IUpdateService update)
    {
        _update = update;
    }

    /// <summary>状态或进度变化时触发（可能在后台线程，订阅方自行切到 UI 线程）。</summary>
    public event EventHandler? Changed;

    public DownloadState State { get; private set; } = DownloadState.Idle;

    /// <summary>0–100。</summary>
    public int Percent { get; private set; }

    /// <summary>给用户看的说明。</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>正在下载（或已下载）的版本号。</summary>
    public string? TargetVersion { get; private set; }

    /// <summary>是否正在下载。</summary>
    public bool IsDownloading => State == DownloadState.Downloading;

    /// <summary>
    /// 开始下载。已经在下载同一个版本时直接返回（窗口反复开关不会重复下）。
    /// </summary>
    public void Start(UpdateManifest info)
    {
        lock (_gate)
        {
            if (State == DownloadState.Downloading)
            {
                return;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            TargetVersion = info.DisplayVersion;

            SetStateLocked(DownloadState.Downloading, 0, $"正在下载 {info.DisplayVersion}…");
        }

        Raise();

        _task = Task.Run(() => RunAsync(info, _cts!.Token));
    }

    /// <summary>取消下载（用户主动取消时用）。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (State != DownloadState.Downloading)
            {
                return;
            }

            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>把已结束的状态复位成 Idle（用户查看过失败原因后）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (State == DownloadState.Downloading)
            {
                return;
            }

            SetStateLocked(DownloadState.Idle, 0, string.Empty);
        }

        Raise();
    }

    private async Task RunAsync(UpdateManifest info, CancellationToken ct)
    {
        try
        {
            // 关键：用同步回调而不是 Progress<T>。
            // Progress<T> 会把 Report 投递到创建它的 SynchronizationContext，
            // 窗口一关 Dispatcher 就没了，Report 会抛异常打断下载。
            var progress = new SyncProgress(OnPercent);

            var result = await _update.DownloadAsync(info, progress, ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                lock (_gate)
                {
                    SetStateLocked(DownloadState.Failed, Percent, result.Message);
                }

                Raise();
                return;
            }

            lock (_gate)
            {
                SetStateLocked(DownloadState.Completed, 100, "下载完成，正在安装新版本…");
            }

            Raise();

            // 让界面把「下载完成」显示出来，再启动新包
            await Task.Delay(700, CancellationToken.None).ConfigureAwait(false);

            InstallNow(result.FilePath);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                SetStateLocked(DownloadState.Idle, 0, "已取消下载。");
            }

            Raise();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                SetStateLocked(DownloadState.Failed, Percent, $"下载失败：{ex.Message}");
            }

            Raise();
        }
    }

    private void OnPercent(int value)
    {
        lock (_gate)
        {
            if (State != DownloadState.Downloading)
            {
                return;
            }

            Percent = Math.Clamp(value, 0, 100);
        }

        Raise();
    }

    private void SetStateLocked(DownloadState state, int percent, string message)
    {
        State = state;
        Percent = Math.Clamp(percent, 0, 100);
        Message = message;
    }

    private void Raise()
    {
        var handler = Changed;

        if (handler is null)
        {
            return;
        }

        // 逐个调用而不是 Changed?.Invoke(...)：
        // 多播委托里第一个订阅者抛异常会中断后面所有人，
        // 那样「一个已关闭的窗口」就能把「另一个还在的窗口」也弄瞎。
        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)target)(this, EventArgs.Empty);
            }
            catch
            {
                // 订阅方（已关闭的窗口）抛异常不能影响下载，也不能影响其他订阅方
            }
        }
    }

    /// <summary>启动新下载的包；它会接手覆盖安装旧版本。</summary>
    private void InstallNow(string filePath)
    {
        try
        {
            if (!_update.Launch(filePath))
            {
                lock (_gate)
                {
                    SetStateLocked(DownloadState.Failed, 100, $"无法启动安装包：{filePath}");
                }

                Raise();
                return;
            }

            // 交给新包接管，本进程退出（新包会等我们释放 exe 后再覆盖）
            var app = Application.Current;

            if (app is null)
            {
                return;
            }

            app.Dispatcher.Invoke(() => app.Shutdown());
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                SetStateLocked(DownloadState.Failed, 100, $"启动安装包失败：{ex.Message}");
            }

            Raise();
        }
    }

    /// <summary>释放（取消进行中的下载）。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 忽略
            }

            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>同步进度：直接调用回调，不经过任何 SynchronizationContext。</summary>
    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _onReport;

        public SyncProgress(Action<int> onReport) => _onReport = onReport;

        public void Report(int value)
        {
            try
            {
                _onReport(value);
            }
            catch
            {
                // 进度回调失败绝不能中断下载
            }
        }
    }
}
