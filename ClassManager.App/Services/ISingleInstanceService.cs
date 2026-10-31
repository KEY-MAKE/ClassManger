namespace ClassManager.App.Services;

/// <summary>单实例服务接口。</summary>
public interface ISingleInstanceService
{
    /// <summary>是否为首个实例。</summary>
    bool IsFirstInstance { get; }

    /// <summary>首实例被第二个实例请求激活时触发。</summary>
    event EventHandler? ActivatedBySecondInstance;

    /// <summary>获取互斥量并判定是否首实例。</summary>
    void Acquire();

    /// <summary>通知首实例激活窗口。</summary>
    void SignalFirstInstance();

    /// <summary>首实例开始后台监听。</summary>
    void StartListening();

    /// <summary>释放资源。</summary>
    void Dispose();
}
