using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClassManager.App.Services;

/// <summary>
/// pip 镜像源统一管理 + 自动轮换。
/// <para>
/// <b>为什么要轮换</b>：不同镜像同步进度不一样。实测 <c>mineru</c> 在阿里云/中科大/腾讯云
/// 都有 4.2.3，但**清华根本没有这个包**（<c>No matching distribution found</c>）。
/// 写死一个源就等于"在某些包上必挂"。
/// </para>
/// <para>
/// 所以这里维护一个有序列表，装包时逐个试，第一个成功就停 —— 既快又不会因为某个源缺包就失败。
/// </para>
/// </summary>
public static class PackageMirrors
{
    /// <summary>一个镜像源。</summary>
    public sealed record Mirror(string Url, string Host);

    /// <summary>
    /// 国内镜像（按"包覆盖度 + 速度"排序）。
    /// <para>阿里云放第一：它同步最全，mineru 这种新包也有。</para>
    /// </summary>
    public static readonly Mirror[] Domestic =
    {
        new("https://mirrors.aliyun.com/pypi/simple/", "mirrors.aliyun.com"),
        new("https://pypi.mirrors.ustc.edu.cn/simple/", "pypi.mirrors.ustc.edu.cn"),
        new("https://mirrors.cloud.tencent.com/pypi/simple/", "mirrors.cloud.tencent.com"),
        new("https://pypi.tuna.tsinghua.edu.cn/simple", "pypi.tuna.tsinghua.edu.cn"),
    };

    /// <summary>官方源（最后一档，前面全挂才用）。</summary>
    public static readonly Mirror Official = new("https://pypi.org/simple", "pypi.org");

    /// <summary>完整候选序列（国内优先，官方兜底）。</summary>
    public static IEnumerable<Mirror> Candidates()
    {
        foreach (var mirror in Domestic)
        {
            yield return mirror;
        }

        yield return Official;
    }

    /// <summary>
    /// 用镜像轮换装包。返回 (是否成功, 最后一次的错误摘要)。
    /// </summary>
    public static async Task<(bool Ok, string Detail)> InstallWithFallbackAsync(
        string pythonExe,
        IReadOnlyList<string> packageArgs,
        IProgress<string>? progress,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var errors = new List<string>();
        var attempt = 0;

        foreach (var mirror in Candidates())
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            progress?.Report($"正在从 {mirror.Host} 安装…（第 {attempt} 个源）");

            var args = new List<string>
            {
                "-m", "pip", "install", "--upgrade",
                "--disable-pip-version-check", "--no-input",
                "--index-url", mirror.Url,
                "--trusted-host", mirror.Host,
            };

            args.AddRange(packageArgs);

            var run = await RunAsync(
                pythonExe,
                args,
                timeout ?? TimeSpan.FromMinutes(30),
                ct).ConfigureAwait(false);

            if (run.Ok)
            {
                return (true, mirror.Host);
            }

            // 「这个源没有这个包」→ 换源立刻能解决，值得继续试
            // 「网络超时/证书错误」→ 下一个源也可能好，同样继续
            var detail = Summarize(run.Error, run.Output);
            errors.Add($"{mirror.Host}: {detail}");

            progress?.Report($"{mirror.Host} 没成功，换下一个源…");
        }

        return (false, errors.Count > 0 ? errors[^1] : "所有镜像源都失败");
    }

    /// <summary>
    /// 把 pip 全局源写成国内镜像，之后任何 pip 操作都快（包括用户手动跑的）。
    /// </summary>
    public static void ConfigureGlobalPipIndex()
    {
        try
        {
            // Windows: %APPDATA%\pip\pip.ini
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "pip");

            Directory.CreateDirectory(dir);

            var ini = Path.Combine(dir, "pip.ini");

            const string content =
                "[global]\n" +
                "index-url = https://mirrors.aliyun.com/pypi/simple/\n" +
                "trusted-host = mirrors.aliyun.com\n" +
                "\n" +
                "[install]\n" +
                "trusted-host = mirrors.aliyun.com\n"
                + "             pypi.mirrors.ustc.edu.cn\n"
                + "             mirrors.cloud.tencent.com\n"
                + "             pypi.tuna.tsinghua.edu.cn\n";

            // 已存在且已经是我配的就不覆盖（尊重用户自己改过的）
            if (File.Exists(ini))
            {
                var existing = File.ReadAllText(ini);

                if (existing.Contains("mirrors.aliyun.com/pypi", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 用户自己配了别的源 → 不动
                if (existing.Contains("index-url", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            File.WriteAllText(ini, content, new UTF8Encoding(false));
        }
        catch
        {
            // 配不上就算了，代码里每次都会显式传 --index-url
        }
    }

    private static async Task<(bool Ok, string Output, string Error)> RunAsync(
        string exe,
        IEnumerable<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return (false, string.Empty, "无法启动进程");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            return (process.ExitCode == 0,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // 忽略
            }

            return (false, string.Empty, ct.IsCancellationRequested ? "已取消" : "超时");
        }
    }

    private static string Summarize(string error, string output)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? output : error;

        if (string.IsNullOrWhiteSpace(detail))
        {
            return "未知错误";
        }

        detail = detail.Trim();

        // 取最后几行，通常才是真正原因
        var lines = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        detail = string.Join(" ", lines.TakeLast(3)).Trim();

        return detail.Length > 200 ? detail[^200..] : detail;
    }
}
