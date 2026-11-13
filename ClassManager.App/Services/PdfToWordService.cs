using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClassManager.App.Services;

/// <summary>
/// PDF → Word 转换。
/// <para>
/// <b>算法部分</b>：用 <c>pdf2docx</c>（基于 PyMuPDF），能还原段落、表格、图片位置，
/// 纯离线、不上传文件 —— 试卷这种敏感内容不能往外发。
/// </para>
/// <para>
/// <b>AI 部分</b>：转换完可以让模型读一遍 docx，修掉 pdf2docx 常见的毛病
/// （段落被拆碎、表格错位、页眉页脚混进正文）。这一步是可选的，
/// 由调用方决定要不要做。
/// </para>
/// </summary>
public sealed class PdfToWordService
{
    /// <summary>pdf2docx 依赖的包不少（PyMuPDF / opencv 等），首次装约 50MB。</summary>
    private const string RequiredModule = "pdf2docx";

    /// <summary>转换结果。</summary>
    public sealed record ConvertResult(
        bool Ok,
        string Message,
        string? OutputPath,
        bool EngineInstalledNow);

    /// <summary>
    /// 把 PDF 转成 docx。
    /// </summary>
    /// <param name="pdfPath">源 PDF 的绝对路径。</param>
    /// <param name="outputPath">输出 docx 绝对路径；传 null 则同名 .docx。</param>
    public async Task<ConvertResult> ConvertAsync(
        string pdfPath,
        string? outputPath = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
        {
            return new ConvertResult(false, $"文件不存在：{pdfPath}", null, false);
        }

        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return new ConvertResult(false, "只能转换 .pdf 文件。", null, false);
        }

        var target = string.IsNullOrWhiteSpace(outputPath)
            ? Path.ChangeExtension(pdfPath, ".docx")
            : outputPath;

        // 1) 需要带 pip 的完整 Python
        progress?.Report("正在准备 Python 环境…");

        var python = await PythonBootstrap.EnsureAsync(
            new Progress<string>(s => progress?.Report(s)), ct).ConfigureAwait(false);

        if (!python.Ok || python.PythonPath is null)
        {
            return new ConvertResult(
                false,
                "PDF 转换需要 Python 环境，自动安装失败：" + python.Message,
                null,
                python.InstalledNow);
        }

        var interpreter = python.PythonPath;

        // 2) 确保 pdf2docx 已安装
        var installedNow = false;

        if (!await HasModuleAsync(interpreter, RequiredModule, ct).ConfigureAwait(false))
        {
            progress?.Report($"正在安装转换引擎 {RequiredModule}（约 50MB，仅首次）…");

            var install = await PipInstallAsync(interpreter, RequiredModule, ct).ConfigureAwait(false);

            if (!install.Ok)
            {
                return new ConvertResult(
                    false,
                    "安装转换引擎失败：" + install.Message + "\n（可以稍后重试，或检查网络）",
                    null,
                    python.InstalledNow);
            }

            installedNow = true;
        }

        // 3) 执行转换
        progress?.Report("正在转换，请稍候…");

        var script =
            "import sys\n" +
            "from pdf2docx import Converter\n" +
            $"cv = Converter({ToPyLiteral(pdfPath)})\n" +
            "try:\n" +
            $"    cv.convert({ToPyLiteral(target)})\n" +
            "finally:\n" +
            "    cv.close()\n" +
            "print('CONVERT_OK')\n";

        var run = await RunPythonAsync(interpreter, script, TimeSpan.FromMinutes(10), ct)
            .ConfigureAwait(false);

        if (!run.Ok || !run.Output.Contains("CONVERT_OK", StringComparison.Ordinal))
        {
            var detail = string.IsNullOrWhiteSpace(run.Error) ? run.Output : run.Error;

            if (detail.Length > 600)
            {
                detail = detail[^600..];
            }

            return new ConvertResult(
                false,
                "转换失败：" + detail.Trim(),
                null,
                python.InstalledNow || installedNow);
        }

        if (!File.Exists(target))
        {
            return new ConvertResult(false, "转换完成但没生成文件，请检查输出目录权限。", null, installedNow);
        }

        var size = new FileInfo(target).Length;

        return new ConvertResult(
            true,
            $"转换完成：{Path.GetFileName(target)}（{size / 1024} KB）",
            target,
            python.InstalledNow || installedNow);
    }

    /// <summary>检查某个 Python 模块能不能 import。</summary>
    private static async Task<bool> HasModuleAsync(string python, string module, CancellationToken ct)
    {
        var run = await RunPythonAsync(
            python,
            $"import {module}\nprint('HAS_MODULE')\n",
            TimeSpan.FromSeconds(60),
            ct).ConfigureAwait(false);

        return run.Ok && run.Output.Contains("HAS_MODULE", StringComparison.Ordinal);
    }

    /// <summary>用国内镜像装包。</summary>
    private static async Task<(bool Ok, string Message)> PipInstallAsync(
        string python,
        string package,
        CancellationToken ct)
    {
        var startInfo = NewPythonStartInfo(python);

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("pip");
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--disable-pip-version-check");
        startInfo.ArgumentList.Add("--no-input");
        startInfo.ArgumentList.Add("--index-url");
        startInfo.ArgumentList.Add("https://pypi.tuna.tsinghua.edu.cn/simple");
        startInfo.ArgumentList.Add("--trusted-host");
        startInfo.ArgumentList.Add("pypi.tuna.tsinghua.edu.cn");
        startInfo.ArgumentList.Add(package);

        var run = await RunProcessAsync(startInfo, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

        if (run.Ok)
        {
            return (true, "ok");
        }

        var detail = string.IsNullOrWhiteSpace(run.Error) ? run.Output : run.Error;

        if (detail.Length > 500)
        {
            detail = detail[^500..];
        }

        return (false, detail.Trim());
    }

    private static ProcessStartInfo NewPythonStartInfo(string python) => new(python)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static async Task<(bool Ok, string Output, string Error)> RunPythonAsync(
        string python,
        string code,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = NewPythonStartInfo(python);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);

        return await RunProcessAsync(startInfo, timeout, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Ok, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return (false, string.Empty, "无法启动进程。");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            return (process.ExitCode == 0, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
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

            return (false, string.Empty, "执行超时。");
        }
    }

    /// <summary>把路径变成 Python 的原始字符串字面量（避免反斜杠转义问题）。</summary>
    private static string ToPyLiteral(string path) => "r\"" + path.Replace("\"", "\\\"") + "\"";
}
