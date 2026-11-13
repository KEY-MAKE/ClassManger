using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace ClassManager.App.Services;

/// <summary>
/// 按需安装 Python 运行时。
/// <para>
/// 背景：程序自带一份嵌入式 Python（课件引擎里那份），但它是裁剪版 ——
/// 没有 pip，装不了 <c>pdf2docx</c> 这类第三方库。而老师们的电脑通常也没装 Python。
/// 所以「AI 要跑需要第三方库的脚本」时，这里负责把完整 Python 装上。
/// </para>
/// <para>
/// 走用户级安装（<c>InstallAllUsers=0</c>），不需要管理员权限，
/// 装到 <c>%LocalAppData%\Programs\Python\Python3xx</c> —— 那个路径
/// <see cref="ToolInstaller.FindPython"/> 本来就会去找。
/// </para>
/// </summary>
public static class PythonBootstrap
{
    /// <summary>要装的版本。</summary>
    private const string Version = "3.12.10";

    /// <summary>清华镜像上的安装包（国内下载快）。</summary>
    private const string InstallerUrl =
        "https://mirrors.tuna.tsinghua.edu.cn/python/3.12.10/python-3.12.10-amd64.exe";

    /// <summary>镜像挂了时回退官方站。</summary>
    private const string FallbackInstallerUrl =
        "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";

    /// <summary>下载超时。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>安装超时。</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(8);

    /// <summary>安装结果。</summary>
    public sealed record InstallResult(bool Ok, string Message, string? PythonPath, bool InstalledNow);

    /// <summary>
    /// 确保有一个**带 pip** 的 Python 可用。
    /// <para>已经有就立刻返回；没有才下载安装（约 25MB，几分钟）。</para>
    /// </summary>
    public static async Task<InstallResult> EnsureAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 1) 已经有可用的（系统装的 / 之前装过的）→ 直接用
        var existing = ToolInstaller.FindPython();

        if (existing is not null && await HasPipAsync(existing, ct).ConfigureAwait(false))
        {
            return new InstallResult(true, "已找到可用的 Python。", existing, false);
        }

        progress?.Report($"正在下载 Python {Version}（约 25MB，仅首次需要）…");

        // 2) 下载安装包
        string installer;

        try
        {
            installer = await DownloadInstallerAsync(progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, "已取消安装。", null, false);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"下载安装包失败：{ex.Message}", null, false);
        }

        try
        {
            // 3) 静默安装（用户级，不需要管理员）
            progress?.Report("正在安装 Python（约 1–2 分钟，请勿关闭程序）…");

            var code = await RunInstallerAsync(installer, ct).ConfigureAwait(false);

            if (code != 0)
            {
                return new InstallResult(false, $"安装程序返回错误码 {code}。", null, false);
            }

            // 4) 重新找一遍并确认 pip 可用
            var installed = ToolInstaller.FindPython();

            if (installed is null)
            {
                return new InstallResult(false, "安装完成但找不到 python.exe，可能需要重启程序。", null, true);
            }

            if (!await HasPipAsync(installed, ct).ConfigureAwait(false))
            {
                return new InstallResult(
                    false,
                    "Python 已安装，但 pip 不可用，无法安装第三方包。",
                    installed,
                    true);
            }

            return new InstallResult(true, "Python 安装完成。", installed, true);
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, "已取消安装。", null, false);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装失败：{ex.Message}", null, false);
        }
        finally
        {
            TryDelete(installer);
        }
    }

    /// <summary>下载安装包到临时目录，优先清华镜像。</summary>
    private static async Task<string> DownloadInstallerAsync(
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var target = Path.Combine(Path.GetTempPath(), $"python-{Version}-amd64.exe");

        // 之前下过且完整就直接用（同一台机器重装/重试时不重复下载）
        if (File.Exists(target) && new FileInfo(target).Length > 20 * 1024 * 1024)
        {
            progress?.Report("已存在下载好的安装包，直接使用。");
            return target;
        }

        using var client = new HttpClient { Timeout = DownloadTimeout };

        var errors = new List<string>();

        foreach (var url in new[] { InstallerUrl, FallbackInstallerUrl })
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var destination = File.Create(target);

                await source.CopyToAsync(destination, ct).ConfigureAwait(false);

                var size = new FileInfo(target).Length;

                if (size < 20 * 1024 * 1024)
                {
                    throw new InvalidOperationException($"下载到的文件太小（{size} 字节），可能不是安装包。");
                }

                return target;
            }
            catch (OperationCanceledException)
            {
                TryDelete(target);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(target);
                errors.Add($"{url} → {ex.Message}");
            }
        }

        throw new InvalidOperationException(string.Join("；", errors));
    }

    /// <summary>静默安装。用户级安装，不弹 UAC。</summary>
    private static async Task<int> RunInstallerAsync(string installer, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(installer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // InstallAllUsers=0 → 装到当前用户目录，不需要管理员
        // PrependPath=1   → 加进 PATH，之后 FindPython 能直接找到
        // Include_pip=1   → 一定要带 pip（否则装不了 pdf2docx 之类）
        startInfo.ArgumentList.Add("/quiet");
        startInfo.ArgumentList.Add("InstallAllUsers=0");
        startInfo.ArgumentList.Add("PrependPath=1");
        startInfo.ArgumentList.Add("Include_test=0");
        startInfo.ArgumentList.Add("Include_pip=1");
        startInfo.ArgumentList.Add("Include_launcher=1");

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return -1;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InstallTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 忽略
            }

            throw;
        }

        return process.ExitCode;
    }

    /// <summary>检查这个 python 有没有 pip。</summary>
    private static async Task<bool> HasPipAsync(string python, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("pip");
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return process.ExitCode == 0 && output.Contains("pip", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就留着，下次还能复用
        }
    }
}
