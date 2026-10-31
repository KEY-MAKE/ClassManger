namespace ClassManager.App.Services;

/// <summary>
/// 检查更新：读一个「更新源」（http(s) 直链或本地/局域网文件）上的 <c>version.json</c>，
/// 与当前版本比对，有新版则下载单文件安装包并交由其覆盖安装。
/// </summary>
public interface IUpdateService
{
    /// <summary>当前更新设置（可直接改字段后调用 <see cref="Save"/>）。</summary>
    UpdateSettings Settings { get; }

    /// <summary>安装包下载目录（%LocalAppData%\ClassManager\updates）。</summary>
    string DownloadsDirectory { get; }

    /// <summary>检查一次更新；未配置更新源时返回 <see cref="UpdateCheckResult.NotConfigured"/>。</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>下载指定版本的安装包（已完成/已存在则直接复用），<paramref name="progress"/> 为 0–100 百分比。</summary>
    Task<UpdateDownloadResult> DownloadAsync(UpdateManifest info, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>启动下载好的安装包（它会自动覆盖安装旧版本）。</summary>
    bool Launch(string filePath);

    /// <summary>
    /// 打开下载页：网页地址用浏览器打开；本地路径用资源管理器打开所在文件夹。
    /// 这样更新源放在网盘/局域网共享上时也能用，不必自己搭服务器。
    /// </summary>
    bool OpenDownloadPage(UpdateManifest info);

    /// <summary>算出「打开下载页」应该跳到哪里（纯计算，不打开任何东西）。</summary>
    UpdateDownloadTarget ResolveDownloadTarget(UpdateManifest info);

    /// <summary>持久化当前设置。</summary>
    void Save();
}
