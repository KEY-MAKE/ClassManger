using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassManager.App.Services;

/// <summary>
/// 评讲课件生成服务实现。
/// 引擎（Python 版 exam2ppt）以 zip 形式内嵌在程序集里（保证单文件 exe 分发不被破坏），
/// 首次使用时解压到 <c>%LocalAppData%\ClassManager\engine\exam2ppt-&lt;版本&gt;\</c>，
/// 之后以子进程方式调用 <c>runtime\python.exe main.py …</c>。
/// </summary>
public sealed partial class Exam2PptService : IExam2PptService
{
    private const string PayloadResourceSuffix = "exam2ppt-payload.zip";
    private const string ReadyMarkerName = ".ready";

    /// <summary>引擎解压互斥（避免并发重复解压）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public string PayloadVersion => "1.0.0";

    /// <inheritdoc />
    public string EngineRoot { get; }

    /// <inheritdoc />
    public string? LastEngineError { get; private set; }

    public Exam2PptService()
    {
        EngineRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassManager",
            "engine",
            $"exam2ppt-{PayloadVersion}");
    }

    /// <inheritdoc />
    public async Task<bool> EnsureEngineAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsReady())
        {
            return true;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady())
            {
                return true;
            }

            LastEngineError = null;
            progress?.Report("正在准备课件引擎（首次使用需解压，约需数秒）…");

            await Task.Run(() => ExtractPayload(progress, ct), ct).ConfigureAwait(false);

            if (!IsReady())
            {
                LastEngineError = "引擎解压后校验失败（缺少 .ready 标记或 python.exe）。";
                return false;
            }

            progress?.Report("课件引擎已就绪。");
            return true;
        }
        catch (OperationCanceledException)
        {
            LastEngineError = "引擎准备被取消。";
            return false;
        }
        catch (Exception ex)
        {
            LastEngineError = $"引擎准备失败：{ex.Message}";
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Exam2PptResult> GenerateAsync(
        Exam2PptOptions options,
        IProgress<Exam2PptProgress>? progress,
        CancellationToken ct)
    {
        var log = new List<string>();
        var logLock = new object();

        void Append(string line)
        {
            lock (logLock)
            {
                log.Add(line);
            }
        }

        if (!await EnsureEngineAsync(null, ct).ConfigureAwait(false))
        {
            return Failure(LastEngineError ?? "引擎未就绪。", log, -1);
        }

        var pythonExe = Path.Combine(EngineRoot, "runtime", "python.exe");
        var mainPy = Path.Combine(EngineRoot, "main.py");
        if (!File.Exists(pythonExe))
        {
            return Failure($"引擎缺少解释器：{pythonExe}", log, -1);
        }

        if (!File.Exists(mainPy))
        {
            return Failure($"引擎缺少入口脚本：{mainPy}", log, -1);
        }

        if (!File.Exists(options.ExamDocxPath))
        {
            return Failure($"试题文件不存在：{options.ExamDocxPath}", log, -1);
        }

        if (!File.Exists(options.AnswerDocxPath))
        {
            return Failure($"答案文件不存在：{options.AnswerDocxPath}", log, -1);
        }

        // 预览模式且未指定输出时，落到临时文件，由调用方清理
        var outputPath = options.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Path.GetTempPath(), $"cm_exam2ppt_{Guid.NewGuid():N}.pptx");
        }

        var startInfo = new ProcessStartInfo(pythonExe)
        {
            WorkingDirectory = EngineRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 逐个参数入列，绝不手工拼引号（中文/空格路径安全）
        startInfo.ArgumentList.Add(mainPy);
        startInfo.ArgumentList.Add("--exam");
        startInfo.ArgumentList.Add(options.ExamDocxPath);
        startInfo.ArgumentList.Add("--answer");
        startInfo.ArgumentList.Add(options.AnswerDocxPath);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(outputPath);

        if (!string.IsNullOrWhiteSpace(options.TemplatePath))
        {
            startInfo.ArgumentList.Add("--template");
            startInfo.ArgumentList.Add(options.TemplatePath);
        }

        if (options.SkipMaterial)
        {
            startInfo.ArgumentList.Add("--no-material");
        }

        if (options.SkipGuide)
        {
            startInfo.ArgumentList.Add("--no-guide");
        }

        if (options.SkipAnalysis)
        {
            startInfo.ArgumentList.Add("--no-analysis");
        }

        if (options.SkipAnswer)
        {
            startInfo.ArgumentList.Add("--no-answer");
        }

        if (!string.IsNullOrWhiteSpace(options.DumpJsonPath))
        {
            startInfo.ArgumentList.Add("--dump-json");
            startInfo.ArgumentList.Add(options.DumpJsonPath);
        }

        // --assets 决定背景图查找目录：不传的话引擎只在试题目录下找 .cache，
        // 自带的 slide_bg.png 就永远用不上（之前一直「未找到，使用纯白背景」）。
        string? tempAssets = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.BackgroundImagePath)
                && File.Exists(options.BackgroundImagePath))
            {
                tempAssets = Path.Combine(Path.GetTempPath(), $"cm_exam2ppt_assets_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempAssets);

                var ext = Path.GetExtension(options.BackgroundImagePath).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg"))
                {
                    ext = ".png";
                }

                File.Copy(options.BackgroundImagePath, Path.Combine(tempAssets, "slide_bg" + ext), true);
                startInfo.ArgumentList.Add("--assets");
                startInfo.ArgumentList.Add(tempAssets);
            }
            else
            {
                var defaultAssets = Path.Combine(EngineRoot, "assets");
                if (Directory.Exists(defaultAssets))
                {
                    startInfo.ArgumentList.Add("--assets");
                    startInfo.ArgumentList.Add(defaultAssets);
                }
            }
        }
        catch
        {
            // 背景准备失败不影响出课件（回落到纯白背景）
        }

        var totalQuestions = 0;
        var slideCount = 0;
        var stage = 0;
        var currentQuestion = 0;
        var hitSeen = false;
        var identified = false;
        var missing = new List<string>();

        void HandleLine(string line)
        {
            Append(line);

            var stageMatch = StageRegex().Match(line);
            if (stageMatch.Success && int.TryParse(stageMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            {
                stage = s;
            }

            if (!hitSeen && line.Contains("命中", StringComparison.Ordinal))
            {
                hitSeen = true;
            }

            if (!identified && line.Contains("识别", StringComparison.Ordinal))
            {
                identified = true;
            }

            var totalMatch = TotalQuestionsRegex().Match(line);
            if (totalMatch.Success && int.TryParse(totalMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            {
                totalQuestions = t;
            }

            var questionMatch = QuestionRegex().Match(line);
            if (questionMatch.Success && int.TryParse(questionMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q))
            {
                currentQuestion = q;
            }

            var slideMatch = SlideCountRegex().Match(line);
            if (slideMatch.Success && int.TryParse(slideMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sc))
            {
                slideCount = sc;
            }

            var missingMatch = MissingRegex().Match(line);
            if (missingMatch.Success)
            {
                foreach (var part in missingMatch.Groups[1].Value.Split(new[] { '、', ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    missing.Add(part.Trim());
                }
            }

            var percent = ComputePercent(stage, currentQuestion, totalQuestions, hitSeen, identified);
            progress?.Report(new Exam2PptProgress(stage, percent, line));
        }

        var tempAssetsDir = tempAssets;
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                HandleLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Append(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return Failure($"无法启动课件引擎：{ex.Message}", log, -1);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = ct.Register(() =>
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
                // 忽略：进程可能已退出
            }
        });

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDeletePartial(outputPath);
            TryDeleteDirectoryQuiet(tempAssetsDir);
            return Failure("已取消生成。", log, -1);
        }

        // 等异步读取把剩余缓冲冲干净
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            TryDeleteDirectoryQuiet(tempAssetsDir);
            progress?.Report(new Exam2PptProgress(4, 100, "完成"));
            return new Exam2PptResult
            {
                Ok = true,
                OutputPath = outputPath,
                SlideCount = slideCount,
                MissingQuestions = missing,
                LogLines = Snapshot(log, logLock),
                ExitCode = 0,
            };
        }

        TryDeletePartial(outputPath);
        TryDeleteDirectoryQuiet(tempAssetsDir);

        var message = FindErrorMessage(log, logLock)
                      ?? $"引擎返回退出码 {process.ExitCode}。";
        return new Exam2PptResult
        {
            Ok = false,
            OutputPath = outputPath,
            Message = message,
            MissingQuestions = missing,
            LogLines = Snapshot(log, logLock),
            ExitCode = process.ExitCode,
        };
    }

    private bool IsReady()
    {
        var marker = Path.Combine(EngineRoot, ReadyMarkerName);
        if (!File.Exists(marker))
        {
            return false;
        }

        try
        {
            var version = File.ReadAllText(marker).Trim();
            if (!string.Equals(version, PayloadVersion, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return File.Exists(Path.Combine(EngineRoot, "runtime", "python.exe"))
               && File.Exists(Path.Combine(EngineRoot, "main.py"));
    }

    private void ExtractPayload(IProgress<string>? progress, CancellationToken ct)
    {
        using var resource = OpenPayloadStream()
                             ?? throw new InvalidOperationException($"程序集内未找到内嵌载荷：{PayloadResourceSuffix}");

        var tempRoot = EngineRoot + ".tmp";
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }

        Directory.CreateDirectory(tempRoot);

        using (var archive = new ZipArchive(resource, ZipArchiveMode.Read, leaveOpen: false))
        {
            var entries = archive.Entries;
            var done = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // 目录项
                }

                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(tempRoot, relative));

                // 防目录穿越
                if (!destination.StartsWith(Path.GetFullPath(tempRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"载荷条目路径非法：{entry.FullName}");
                }

                var folder = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                entry.ExtractToFile(destination, overwrite: true);

                done++;
                if (done % 100 == 0)
                {
                    progress?.Report($"正在解压引擎… {done}/{entries.Count}");
                }
            }
        }

        File.WriteAllText(Path.Combine(tempRoot, ReadyMarkerName), PayloadVersion);

        if (Directory.Exists(EngineRoot))
        {
            Directory.Delete(EngineRoot, true);
        }

        var parent = Path.GetDirectoryName(EngineRoot);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Directory.Move(tempRoot, EngineRoot);
    }

    private static Stream? OpenPayloadStream()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(PayloadResourceSuffix, StringComparison.OrdinalIgnoreCase));

        return name is null ? null : assembly.GetManifestResourceStream(name);
    }

    /// <summary>
    /// 进度估算：引擎实际只有 4 个阶段、总耗时约 2 秒，若只按阶段跳变会显得"不动"。
    /// 这里把阶段内的关键行（命中 / 识别 / 逐题排版）也作为里程碑，让进度条连续推进。
    /// </summary>
    private static double ComputePercent(
        int stage,
        int currentQuestion,
        int totalQuestions,
        bool hitSeen,
        bool identified)
    {
        return stage switch
        {
            <= 1 => hitSeen ? 20 : 6,
            2 => identified ? 40 : 26,
            3 => 45,
            4 => totalQuestions > 0
                ? 50 + Math.Clamp(currentQuestion / (double)totalQuestions, 0, 1) * 45
                : 52,
            _ => 96,
        };
    }

    private static Exam2PptResult Failure(string message, List<string> log, int exitCode)
    {
        return new Exam2PptResult
        {
            Ok = false,
            Message = message,
            LogLines = log.ToArray(),
            ExitCode = exitCode,
        };
    }

    private static List<string> Snapshot(List<string> log, object gate)
    {
        lock (gate)
        {
            return new List<string>(log);
        }
    }

    private static string? FindErrorMessage(List<string> log, object gate)
    {
        lock (gate)
        {
            for (var i = log.Count - 1; i >= 0; i--)
            {
                var line = log[i].Trim();
                if (line.StartsWith("[错误]", StringComparison.Ordinal))
                {
                    return line;
                }
            }

            for (var i = log.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(log[i]))
                {
                    return log[i].Trim();
                }
            }
        }

        return null;
    }

    private static void TryDeleteDirectoryQuiet(string? dir)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void TryDeletePartial(string path)
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
            // 忽略
        }
    }

    [GeneratedRegex(@"\[(\d)/4\]")]
    private static partial Regex StageRegex();

    [GeneratedRegex(@"题目\s*(\d+)\s*道")]
    private static partial Regex TotalQuestionsRegex();

    [GeneratedRegex(@"排版第\s*(\d+)\s*题")]
    private static partial Regex QuestionRegex();

    [GeneratedRegex(@"共\s*(\d+)\s*页幻灯片")]
    private static partial Regex SlideCountRegex();

    [GeneratedRegex(@"未匹配到答案/解析的题[：:]\s*(.+)$")]
    private static partial Regex MissingRegex();
}
