namespace ClassManager.App.Models;

/// <summary>安装完整性状态。</summary>
public enum InstallState
{
    /// <summary>没有安装记录（首次安装）。</summary>
    NotInstalled = 0,

    /// <summary>已安装且完整、版本一致。</summary>
    Healthy = 1,

    /// <summary>有安装记录但文件不完整（例如安装目录里的 exe 不见了）。</summary>
    Broken = 2,

    /// <summary>已安装但版本与当前程序不同（需要覆盖升级）。</summary>
    Outdated = 3,
}

/// <summary>一次安装完整性检查的结果。</summary>
public sealed class InstallStatus
{
    public InstallState State { get; init; }

    /// <summary>注册表记录的安装目录（可能为空）。</summary>
    public string InstallDir { get; init; } = string.Empty;

    /// <summary>安装目录中的主程序路径。</summary>
    public string ExePath { get; init; } = string.Empty;

    /// <summary>注册表记录的版本。</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>主程序实际大小（字节；不存在为 0）。</summary>
    public long ExeLength { get; init; }

    /// <summary>人类可读的说明（同时写入安装日志）。</summary>
    public string Detail { get; init; } = string.Empty;
}
