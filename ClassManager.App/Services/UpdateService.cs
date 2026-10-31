using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace ClassManager.App.Services;

/// <inheritdoc cref="IUpdateService"/>
public sealed class UpdateService : IUpdateService
{
    /// <summary>
    /// 直连（不走系统代理）。
    /// <para>
    /// 重要：HttpClient 默认会走系统代理（Windows 的 IE/系统代理设置）。实测很多代理对
    /// github.com 的 release 附件（重定向到 objects.githubusercontent.com）会返回 502，
    /// 于是「能 ping 通 github 但下载失败」。所以更新相关请求一律先直连，
    /// 直连不通再回退到系统代理（公司网络必须走代理的场景也能用）。
    /// </para>
    /// </summary>
    private static readonly HttpClient HttpDirect = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = false,
        AllowAutoRedirect = true,

        // 连不上要快速失败，才能尽快换下一个地址（默认 100 秒太慢）
        ConnectTimeout = TimeSpan.FromSeconds(8),
    })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    /// <summary>走系统代理的备用通道。</summary>
    private static readonly HttpClient HttpProxy = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    /// <summary>读小文件（清单）用的通道：直连优先。</summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = false,
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly UpdateSettingsStore _store;
    private readonly UpdateSettings _settings;

    /// <param name="baseDirectory">设置存储目录；传 null 用 <c>%AppData%\ClassManager</c>。</param>
    /// <param name="downloadsDirectory">安装包下载目录；传 null 用 <c>%LocalAppData%\ClassManager\updates</c>。</param>
    public UpdateService(string? baseDirectory = null, string? downloadsDirectory = null)
    {
        _store = new UpdateSettingsStore(baseDirectory);
        _settings = _store.Load();

        DownloadsDirectory = string.IsNullOrWhiteSpace(downloadsDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassManager",
                "updates")
            : downloadsDirectory;
    }

    public UpdateSettings Settings => _settings;

    public string DownloadsDirectory { get; }

    public void Save() => _store.Save(_settings);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var source = _settings.SourceUrl?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            return UpdateCheckResult.NoConfig();
        }

        string json;
        try
        {
            json = await ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail($"无法读取更新源：{ex.Message}");
        }

        var manifest = ParseManifest(json, source);
        if (manifest is null)
        {
            return UpdateCheckResult.Fail("更新源格式不正确：缺少 version / url 字段。");
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var compared = CompareVersions(manifest.Version, AppVersion.Current);

        if (compared <= 0)
        {
            _settings.LastCheckedText = $"{stamp} · 已是最新（{AppVersion.Display}）";
            Save();
            return UpdateCheckResult.Latest($"当前 {AppVersion.Display} 已是最新版本。");
        }

        if (string.Equals(manifest.Version, _settings.IgnoredVersion, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheckResult.Latest($"已忽略 {manifest.DisplayVersion}（可在更新源设置里取消忽略）。");
        }

        _settings.LastCheckedText = $"{stamp} · 发现 {manifest.DisplayVersion}";
        Save();

        var message = manifest.PublishedAt.Length > 0
            ? $"发现新版本 {manifest.DisplayVersion}（{manifest.PublishedAt}）"
            : $"发现新版本 {manifest.DisplayVersion}";

        return UpdateCheckResult.Found(manifest, message);
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateManifest info,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(DownloadsDirectory);

            var safeVersion = string.Concat(info.Version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
            if (safeVersion.Length == 0)
            {
                safeVersion = "latest";
            }

            var target = Path.Combine(DownloadsDirectory, $"ClassManager.App-{safeVersion}.exe");

            if (File.Exists(target) && Verify(target, info, out _))
            {
                progress?.Report(100);
                return UpdateDownloadResult.Success(target);
            }

            // 候选地址：主地址 + 镜像（按顺序回退）
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(info.Url))
            {
                candidates.Add(info.Url);
            }

            candidates.AddRange(info.Mirrors.Where(m => !string.IsNullOrWhiteSpace(m)));

            if (candidates.Count == 0)
            {
                return UpdateDownloadResult.Fail("更新清单里没有下载地址。");
            }

            var failures = new List<string>();

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsHttpUrl(candidate))
                {
                    var local = ResolveLocalPath(candidate);

                    if (!File.Exists(local))
                    {
                        failures.Add($"{candidate}：文件不存在");
                        continue;
                    }

                    File.Copy(local, target, overwrite: true);
                    progress?.Report(100);

                    if (!Verify(target, info, out var localReason))
                    {
                        TryDelete(target);
                        failures.Add($"{candidate}：{localReason}");
                        continue;
                    }

                    return UpdateDownloadResult.Success(target);
                }

                var attempt = await DownloadHttpAsync(candidate, target, info, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (attempt.Ok)
                {
                    return attempt;
                }

                failures.Add($"{candidate}：{attempt.Message}");
            }

            return UpdateDownloadResult.Fail("所有下载地址都失败了：\n" + string.Join("\n", failures));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UpdateDownloadResult.Fail($"下载失败：{ex.Message}");
        }
    }

    /// <summary>并发分片的路数。国内直连 GitHub 实测单连接只有 14KB/s，
    /// 8 路 152KB/s、16 路 285KB/s（实测），所以默认开 16 路 Range 请求。</summary>
    private const int DownloadParts = 16;

    private async Task<UpdateDownloadResult> DownloadHttpAsync(
        string url,
        string target,
        UpdateManifest info,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var temp = target + ".part";
        var failures = new List<string>();

        // 直连优先；直连不通再试系统代理
        foreach (var client in new[] { HttpDirect, HttpProxy })
        {
            ct.ThrowIfCancellationRequested();
            TryDelete(temp);

            try
            {
                var (total, supportsRange) = await ProbeAsync(url, info.SizeBytes, client, ct).ConfigureAwait(false);

                if (supportsRange && total > 0)
                {
                    await DownloadSegmentedAsync(url, temp, total, progress, client, ct).ConfigureAwait(false);
                }
                else
                {
                    await DownloadSingleAsync(url, temp, total, progress, client, ct).ConfigureAwait(false);
                }

                if (File.Exists(target))
                {
                    TryDelete(target);
                }

                File.Move(temp, target);

                if (!Verify(target, info, out var reason))
                {
                    TryDelete(target);
                    return UpdateDownloadResult.Fail(reason);
                }

                progress?.Report(100);
                return UpdateDownloadResult.Success(target);
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                failures.Add(ex.Message);
            }
        }

        return UpdateDownloadResult.Fail(string.Join("；", failures));
    }

    /// <summary>探一下总大小，以及服务端是否支持 Range（支持就能分片加速）。</summary>
    private async Task<(long Total, bool SupportsRange)> ProbeAsync(string url, long knownSize, HttpClient client, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { Length: { } length })
            {
                return (length, true);
            }

            return (response.Content.Headers.ContentLength ?? knownSize, false);
        }
        catch
        {
            return (knownSize, false);
        }
    }

    /// <summary>16 路并发 Range 下载，各自写进文件自己的位置。</summary>
    private async Task DownloadSegmentedAsync(
        string url,
        string temp,
        long total,
        IProgress<int>? progress,
        HttpClient client,
        CancellationToken ct)
    {
        var chunk = (total + DownloadParts - 1) / DownloadParts;

        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            file.SetLength(total);
        }

        long done = 0;
        var tasks = new List<Task>();

        for (var i = 0; i < DownloadParts; i++)
        {
            var start = i * chunk;
            var end = Math.Min(total - 1, start + chunk - 1);

            if (start > end)
            {
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                using var fs = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                fs.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[81920];
                int count;
                var written = 0L;

                while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    written += count;
                }

                var current = Interlocked.Add(ref done, written);
                progress?.Report((int)Math.Min(99, current * 100 / total));
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>服务端不支持 Range 时的单连接下载。</summary>
    private async Task DownloadSingleAsync(
        string url,
        string temp,
        long total,
        IProgress<int>? progress,
        HttpClient client,
        CancellationToken ct)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var expected = response.Content.Headers.ContentLength ?? total;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int count;

        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            read += count;

            if (expected > 0)
            {
                progress?.Report((int)Math.Min(99, read * 100 / expected));
            }
        }
    }

    /// <summary>
    /// 打开下载页。网页地址 → 浏览器；本地路径 → 资源管理器打开所在文件夹。
    /// </summary>
    public bool OpenDownloadPage(UpdateManifest info)
    {
        var target = ResolveDownloadTarget(info);

        if (string.IsNullOrEmpty(target.Path))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.Path)
            {
                UseShellExecute = true,
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 算出「打开下载页」跳到哪里：网页地址交给浏览器；本地路径交给资源管理器（打开所在文件夹）。
    /// </summary>
    public UpdateDownloadTarget ResolveDownloadTarget(UpdateManifest info)
    {
        // 优先用清单里专门的下载页；没有就用安装包地址
        var target = string.IsNullOrWhiteSpace(info.PageUrl) ? info.Url : info.PageUrl;

        if (string.IsNullOrWhiteSpace(target))
        {
            return new UpdateDownloadTarget(false, string.Empty);
        }

        if (IsWebUrl(target))
        {
            return new UpdateDownloadTarget(true, target);
        }

        // 本地路径：打开它所在的文件夹，让用户自己下载/替换
        var full = ResolveLocal(target);

        var directory = System.IO.Directory.Exists(full)
            ? full
            : Path.GetDirectoryName(full);

        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
        {
            return new UpdateDownloadTarget(false, string.Empty);
        }

        return new UpdateDownloadTarget(false, directory);
    }

    private static bool IsWebUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>把清单里的相对路径解析成完整路径（相对更新源所在目录）。</summary>
    private string ResolveLocal(string value)
    {
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (Path.IsPathRooted(value))
        {
            return value;
        }

        var source = _settings.SourceUrl;

        if (!string.IsNullOrWhiteSpace(source) && !IsWebUrl(source))
        {
            var sourceFull = ResolveLocal(source);
            var dir = Path.GetDirectoryName(sourceFull);

            if (!string.IsNullOrWhiteSpace(dir))
            {
                return Path.GetFullPath(Path.Combine(dir, value));
            }
        }

        return Path.GetFullPath(value);
    }

    public bool Launch(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
            {
                UseShellExecute = true,
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析更新清单 JSON；字段缺失返回 null。容忍常见别名（GitHub/Gitee 风格）。</summary>
    public static UpdateManifest? ParseManifest(string json, string? baseUrl = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var version = ReadString(root, "version", "latestVersion", "latest_version", "tag_name", "tag");
            if (version.Length == 0)
            {
                return null;
            }

            version = version.Trim().TrimStart('v', 'V');

            var url = ReadString(root, "url", "downloadUrl", "download_url", "packageUrl", "package_url");
            var size = ReadInt64(root, "size", "sizeBytes", "size_bytes", "bytes");

            // GitHub Releases API：下载地址不在顶层，而在 assets[].browser_download_url
            if (root.TryGetProperty("assets", out var assetsElement)
                && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var browserUrl)
                        && browserUrl.ValueKind == JsonValueKind.String
                        && browserUrl.GetString() is { Length: > 0 } assetUrl)
                    {
                        if (url.Length == 0)
                        {
                            url = assetUrl;
                        }

                        if (size == 0 && asset.TryGetProperty("size", out var assetSize)
                            && assetSize.ValueKind == JsonValueKind.Number
                            && assetSize.TryGetInt64(out var assetSizeValue))
                        {
                            size = assetSizeValue;
                        }

                        break;
                    }
                }
            }

            if (url.Length == 0)
            {
                return null;
            }

            // 备用下载地址
            var mirrors = new List<string>();

            if (root.TryGetProperty("mirrors", out var mirrorsElement)
                && mirrorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var mirror in mirrorsElement.EnumerateArray())
                {
                    if (mirror.ValueKind == JsonValueKind.String
                        && mirror.GetString() is { Length: > 0 } mirrorUrl)
                    {
                        mirrors.Add(ResolveUrl(mirrorUrl, baseUrl));
                    }
                }
            }

            var manifest = new UpdateManifest
            {
                Version = version,
                Url = ResolveUrl(url, baseUrl),
                Notes = ReadString(root, "notes", "changelog", "description", "body"),
                PageUrl = ReadString(root, "page", "pageUrl", "page_url", "downloadPage", "download_page"),
                Sha256 = ReadString(root, "sha256", "sha", "hash"),
                PublishedAt = ReadString(root, "publishedAt", "published_at", "date", "releaseDate"),
                SizeBytes = size,
                Mirrors = mirrors,
            };

            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>版本号比较：左 &gt; 右 返回正数。按 System.Version 语义（0.11 &gt; 0.1）。</summary>
    public static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var a) && Version.TryParse(right, out var b))
        {
            return a.CompareTo(b);
        }

        // 兜底：按点分段逐段数值比较
        var la = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var lb = right.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            var va = i < la.Length && long.TryParse(la[i], out var x) ? x : 0;
            var vb = i < lb.Length && long.TryParse(lb[i], out var y) ? y : 0;
            if (va != vb)
            {
                return va > vb ? 1 : -1;
            }
        }

        return 0;
    }

    private static async Task<string> ReadSourceAsync(string source, CancellationToken cancellationToken)
    {
        if (IsHttpUrl(source))
        {
            // 加时间戳破 CDN 缓存：jsDelivr / GitHub 对同一 URL 会缓存很久，
            // 不破缓存的话发了新版用户还看到旧版本号。
            var url = AddCacheBuster(source);

            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        var path = ResolveLocalPath(source);
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>给地址加一个时间戳参数，绕开 CDN 缓存。</summary>
    internal static string AddCacheBuster(string url)
    {
        var separator = url.Contains('?') ? '&' : '?';

        return $"{url}{separator}_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static bool IsHttpUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>把可能是 file:// / 相对路径 / 含环境变量的地址，解析成本地绝对路径。</summary>
    private static string ResolveLocalPath(string value)
    {
        var path = value;

        if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            path = path["file:///".Length..];
        }
        else if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = path["file://".Length..];
        }

        path = Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetFullPath(path);
    }

    /// <summary>相对下载地址按更新源补全；本地源则按同目录解析。</summary>
    private static string ResolveUrl(string url, string? baseUrl)
    {
        if (IsHttpUrl(url) || string.IsNullOrWhiteSpace(baseUrl))
        {
            return url;
        }

        if (IsHttpUrl(baseUrl))
        {
            try
            {
                return new Uri(new Uri(baseUrl), url).ToString();
            }
            catch
            {
                return url;
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(ResolveLocalPath(baseUrl));
            return string.IsNullOrEmpty(dir) ? url : Path.GetFullPath(Path.Combine(dir, url));
        }
        catch
        {
            return url;
        }
    }

    /// <summary>体积 / SHA256 校验（清单没给就跳过对应项）。</summary>
    private static bool Verify(string path, UpdateManifest info, out string reason)
    {
        reason = string.Empty;

        try
        {
            var length = new FileInfo(path).Length;
            if (info.SizeBytes > 0 && length != info.SizeBytes)
            {
                reason = $"安装包大小不符（期望 {info.SizeBytes:N0} 字节，实际 {length:N0} 字节）。";
                return false;
            }

            if (info.Sha256.Length > 0)
            {
                using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(SHA256.HashData(stream));

                if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "安装包 SHA256 校验不通过。";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"校验失败：{ex.Message}";
            return false;
        }
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind is JsonValueKind.Number)
            {
                return value.ToString();
            }
        }

        return string.Empty;
    }

    private static long ReadInt64(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}
