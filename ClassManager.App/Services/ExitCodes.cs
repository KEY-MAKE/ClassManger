namespace ClassManager.App.Services;

/// <summary>进程退出码约定（QA 自动化断言依据）。</summary>
public static class ExitCodes
{
    /// <summary>正常。</summary>
    public const int Ok = 0;

    /// <summary>初始化 / 操作失败。</summary>
    public const int Error = 1;

    /// <summary>已有实例在运行，已请求激活其窗口后退出。</summary>
    public const int AlreadyRunning = 3;
}
