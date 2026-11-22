using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClassManager.App.Services;

/// <summary>
/// 高质量 PDF 解析（可选）：用 MinerU 把 PDF 转成 Markdown，再交给本地引擎写成 docx。
/// <para>
/// <b>为什么单独做一层</b>：MinerU 的强项是「提取」—— 表格、公式、多栏、CJK 都比
/// pdf2docx 强得多，但它的依赖不小（pip 包 + basic 档模型约 0.8GB）。
/// 所以做成可选：老师不急着用的时候可以慢慢下，装过一次就一直有。
/// </para>
/// <para>
/// <b>只取 Markdown</b>：MinerU 4.x 虽然支持 DOCX 渲染，但 CLI 参数在不同版本间
/// 有变动；这里只让它出 Markdown，再用项目里已验证的 <see cref="OfficeWriter"/>
/// 写 docx —— 链路短、可控。
/// </para>
/// </summary>
public sealed class MineruService
{
    /// <summary>pip 包名（4.x 分支）。</summary>
    private const string PackageSpec = "mineru>=4.0,<5";

    /// <summary>模型档位：basic ≈ 0.8GB，CPU 可跑，性价比最高。</summary>
    private const string DefaultTier = "basic";

    /// <summary>装好之后写这个标记，启动时据此判断「已安装」（避免每次都去跑命令探测）。</summary>
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassManager",
        "mineru-ready.txt");

    /// <summary>是否已安装可用（看标记 + 包是否还在）。</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                if (!File.Exists(MarkerPath))
                {
                    return false;
                }

                var python = ToolInstaller.FindPython();

                return python is not null && HasModule(python);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>上次安装失败的原因（给界面显示）。</summary>
    public string? LastError { get; private set; }

    /// <summary>安装结果。</summary>
    public sealed record InstallOutcome(bool Ok, string Message);

    /// <summary>
    /// 安装 MinerU + basic 模型。耗时较长（pip 包几百 MB + 模型 0.8GB）。
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            // 1) 确保有带 pip 的 Python
            progress?.Report("正在准备 Python 环境…");

            var python = await PythonBootstrap.EnsureAsync(null, ct).ConfigureAwait(false);

            if (!python.Ok || python.PythonPath is null)
            {
                LastError = python.Message;
                return new InstallOutcome(false, python.Message);
            }

            // 2) pip 装包（多镜像轮换：阿里云 → 中科大 → 腾讯云 → 清华 → 官方）
            //    实测清华**没有** mineru 这个包，写死单源会直接失败
            progress?.Report($"正在安装 MinerU（{PackageSpec}，约 500MB，请耐心等）…");

            var install = await PackageMirrors.InstallWithFallbackAsync(
                python.PythonPath,
                new[] { PackageSpec },
                progress,
                ct,
                TimeSpan.FromMinutes(30)).ConfigureAwait(false);

            if (!install.Ok)
            {
                LastError = install.Detail;
                return new InstallOutcome(false, "安装 MinerU 失败：" + LastError);
            }

            // 关键一步：MinerU 的命令行用了 `str | None` 这种写法，
            // 老版本 typer（0.12 及以下）不认，会直接抛
            // 「RuntimeError: Type not yet supported: str | None」，所有 mineru 命令都跑不起来。
            // 用户机器上常被别的包（比如 nonebot 那套）把 typer 压到旧版本，所以这里必须纠正。
            progress?.Report("正在校正 typer 版本（MinerU 命令依赖）…");

            var typerFix = await PackageMirrors.InstallWithFallbackAsync(
                python.PythonPath,
                new[] { "typer>=0.15" },
                progress,
                ct,
                TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            if (!typerFix.Ok)
            {
                LastError = typerFix.Detail;
                return new InstallOutcome(false, "校正 typer 版本失败：" + LastError);
            }

            // 3) 下载 basic 档模型（约 0.8GB）
            progress?.Report("正在下载解析模型（basic 档，约 0.8GB）…");

            // 4.2.3 的入口是 console_scripts：Scripts\mineru-models-download.exe
            // 用法：mineru-models-download --tier basic --source auto
            // （不是早期文档写的 "mineru-kit models download"）
            var modelsTool = FindConsoleScript(python.PythonPath, "mineru-models-download");

            if (modelsTool is null)
            {
                LastError = "找不到 mineru-models-download 命令（包可能没装完整）";
                return new InstallOutcome(false, LastError);
            }

            // --small-backend onnx：走 ONNX 运行时，CPU 上就能跑（torch 后端要 GPU 才快）
            // --source modelscope：国内下载比 huggingface 稳
            var models = await RunAsync(
                modelsTool,
                new[] { "--tier", DefaultTier, "--small-backend", "onnx", "--source", "modelscope" },
                TimeSpan.FromMinutes(40),
                ct).ConfigureAwait(false);

            if (!models.Ok)
            {
                LastError = Summarize(models.Error, models.Output);
                return new InstallOutcome(false, "下载模型失败：" + LastError
                    + "\n（包已装好，稍后可以只重试下载模型）");
            }

            // 4) 写标记
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            progress?.Report("高质量模式已就绪。");
            return new InstallOutcome(true, "高质量模式安装完成。");
        }
        catch (OperationCanceledException)
        {
            LastError = "已取消";
            return new InstallOutcome(false, "安装已取消。");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new InstallOutcome(false, "安装出错：" + ex.Message);
        }
    }

    /// <summary>
    /// 把 PDF 解析成 Markdown。返回 null 表示失败。
    /// </summary>
    public async Task<string?> ConvertToMarkdownAsync(
        string pdfPath,
        string outputDirectory,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            var python = ToolInstaller.FindPython();

            if (python is null)
            {
                LastError = "找不到 Python";
                return null;
            }

            Directory.CreateDirectory(outputDirectory);

            progress?.Report("正在用 MinerU 解析（CPU 上每页约几秒）…");

            // 4.2.3：mineru-kit parse <input> -o <output> --tier basic
            var kitTool = FindConsoleScript(python, "mineru-kit");

            if (kitTool is null)
            {
                LastError = "找不到 mineru-kit 命令（包可能没装完整）";
                return null;
            }

            var run = await RunAsync(
                kitTool,
                new[] { "parse", pdfPath, "-o", outputDirectory, "-f", "markdown", "--tier", DefaultTier },
                TimeSpan.FromMinutes(30),
                ct).ConfigureAwait(false);

            if (!run.Ok)
            {
                LastError = Summarize(run.Error, run.Output);
                return null;
            }

            // 找产物：优先同名 .md，否则目录里最新的 .md
            var md = FindMarkdown(outputDirectory, Path.GetFileNameWithoutExtension(pdfPath));

            if (md is null)
            {
                LastError = "解析完成但没找到 Markdown 产物";
                return null;
            }

            return File.ReadAllText(md);
        }
        catch (OperationCanceledException)
        {
            LastError = "已取消";
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>卸载标记（调试用，界面上不暴露）。</summary>
    public void ClearMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch
        {
            // 忽略
        }
    }

    // ---------- 内部 ----------

    /// <summary>找 Python 安装目录下 Scripts 里的 console script（如 mineru-kit.exe）。</summary>
    private static string? FindConsoleScript(string python, string name)
    {
        try
        {
            var pythonDir = Path.GetDirectoryName(python);

            if (string.IsNullOrEmpty(pythonDir))
            {
                return null;
            }

            var scripts = Path.Combine(pythonDir, "Scripts");

            if (!Directory.Exists(scripts))
            {
                return null;
            }

            foreach (var candidate in new[] { name + ".exe", name + ".cmd", name })
            {
                var path = Path.Combine(scripts, candidate);

                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static bool HasModule(string python)
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

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import mineru; print('OK')");

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 忽略
                }

                return false;
            }

            return process.ExitCode == 0 && output.Contains("OK", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindMarkdown(string directory, string baseName)
    {
        try
        {
            var exact = Directory
                .EnumerateFiles(directory, "*.md", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                    .Equals(baseName, StringComparison.OrdinalIgnoreCase));

            return exact ?? Directory
                .EnumerateFiles(directory, "*.md", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
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

            return (false, string.Empty, ct.IsCancellationRequested ? "已取消" : "执行超时");
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

        return detail.Length > 400 ? detail[^400..] : detail;
    }
}
