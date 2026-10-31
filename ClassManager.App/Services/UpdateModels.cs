namespace ClassManager.App.Services;

/// <summary>更新相关本地设置（独立小文件 update.json，不污染 settings.json）。</summary>
public sealed class UpdateSettings
{
    /// <summary>
    /// 更新源地址：可以是 http(s) 上的 version.json 直链，也可以是本地/局域网路径
    /// （如共享盘、网盘同步目录里的 version.json）。留空表示未配置。
    /// </summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>启动时是否静默检查一次更新。</summary>
    public bool AutoCheckOnStartup { get; set; } = true;

    /// <summary>用户选择「忽略此版本」后记录的版本号。</summary>
    public string IgnoredVersion { get; set; } = string.Empty;

    /// <summary>上次检查结果的展示文本（如「2026-10-31 19:22 · 已是最新」）。</summary>
    public string LastCheckedText { get; set; } = string.Empty;
}

/// <summary>「打开下载页」最终要跳到哪：网页（浏览器）还是本地文件夹（资源管理器）。</summary>
public readonly record struct UpdateDownloadTarget(bool IsWeb, string Path);

/// <summary>更新清单（对应更新源里的 version.json）。</summary>
public sealed class UpdateManifest
{
    /// <summary>版本号，如 "0.19"。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>更新说明。</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>安装包直链（新版单文件 exe）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 下载页地址（可选）。填了它，检查到新版后点「打开下载页」会用浏览器打开这个页面，
    /// 由用户自己下载——这样更新源放在网盘/分享链接上也能用，不需要自己搭服务器。
    /// </summary>
    public string PageUrl { get; set; } = string.Empty;

    /// <summary>
    /// 备用下载地址（按顺序尝试）。主地址失败时自动换下一个，
    /// 这样同一个版本可以同时挂在 GitHub、网盘、对象存储上。
    /// </summary>
    public List<string> Mirrors { get; set; } = new();

    /// <summary>安装包字节数（可选，用于完整性校验与显示）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>安装包 SHA256（可选，小写或大写十六进制均可）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>发布日期文本（可选）。</summary>
    public string PublishedAt { get; set; } = string.Empty;

    /// <summary>界面展示版本号，如 "v0.19"。</summary>
    public string DisplayVersion => "v" + Version;

    /// <summary>体积展示文本；未知体积返回空串。</summary>
    public string SizeText => SizeBytes > 0 ? $"{SizeBytes / 1024.0 / 1024.0:F1} MB" : string.Empty;
}

/// <summary>一次检查更新的结果。</summary>
public sealed class UpdateCheckResult
{
    /// <summary>检查过程本身是否成功（失败时看 <see cref="Message"/>）。</summary>
    public bool Ok { get; init; }

    /// <summary>确实存在更新的版本。</summary>
    public bool HasUpdate { get; init; }

    /// <summary>更新清单（仅 <see cref="HasUpdate"/> 为 true 时有值）。</summary>
    public UpdateManifest? Info { get; init; }

    /// <summary>面向用户的说明文本。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>尚未配置更新源（界面应引导去配置）。</summary>
    public bool NotConfigured { get; init; }

    public static UpdateCheckResult Fail(string message) => new() { Ok = false, Message = message };

    public static UpdateCheckResult NoConfig() =>
        new() { Ok = true, NotConfigured = true, Message = "尚未配置更新源" };

    public static UpdateCheckResult Latest(string message) => new() { Ok = true, Message = message };

    public static UpdateCheckResult Found(UpdateManifest info, string message) =>
        new() { Ok = true, HasUpdate = true, Info = info, Message = message };
}

/// <summary>下载新版安装包的结果。</summary>
public sealed class UpdateDownloadResult
{
    public bool Ok { get; init; }

    /// <summary>本地安装包路径（成功时有值）。</summary>
    public string FilePath { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public static UpdateDownloadResult Fail(string message) => new() { Ok = false, Message = message };

    public static UpdateDownloadResult Success(string path) => new() { Ok = true, FilePath = path };
}
