using System.Threading;

namespace ClassManager.App.Services;

/// <summary>
/// 单实例实现（单一方案）：命名 Mutex 保证唯一性 + 命名 EventWaitHandle 跨进程信号。
/// </summary>
public sealed class SingleInstanceService : ISingleInstanceService
{
    private const string MutexName = @"Global\ClassManager.SingleInstance.9F1B7C2E-1A34-4B7E-8F2C-7D5A6E3B9C10";
    private const string EventName = @"Local\ClassManager.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _event;
    private Thread? _listener;
    private volatile bool _running;

    /// <inheritdoc />
    public bool IsFirstInstance { get; private set; }

    /// <inheritdoc />
    public event EventHandler? ActivatedBySecondInstance;

    /// <inheritdoc />
    public void Acquire()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
        }
        catch (AbandonedMutexException)
        {
            // 上一实例异常退出，本进程视为首实例
            IsFirstInstance = true;
        }

        if (IsFirstInstance)
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
    }

    /// <inheritdoc />
    public void SignalFirstInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(EventName);
            handle.Set();
        }
        catch
        {
            // 首实例尚未创建事件句柄时忽略
        }
    }

    /// <inheritdoc />
    public void StartListening()
    {
        if (!IsFirstInstance || _event is null)
        {
            return;
        }

        _running = true;
        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "ClassManager.SingleInstance.Listener",
        };
        _listener.Start();
    }

    private void ListenLoop()
    {
        while (_running)
        {
            try
            {
                if (_event!.WaitOne(500))
                {
                    ActivatedBySecondInstance?.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
                // 事件句柄被释放时退出监听
                return;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _running = false;

        try
        {
            _listener?.Join(300);
        }
        catch
        {
            // 忽略
        }

        _event?.Dispose();
        _event = null;

        if (_mutex is not null)
        {
            if (IsFirstInstance)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // 忽略：可能未持有
                }
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
