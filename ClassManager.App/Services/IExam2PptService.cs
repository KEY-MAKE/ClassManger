namespace ClassManager.App.Services;

/// <summary>评讲课件生成参数（映射到 exam2ppt 的 CLI 参数）。</summary>
public sealed class Exam2PptOptions
{
    /// <summary>试题原卷 docx。</summary>
    public string ExamDocxPath { get; init; } = string.Empty;

    /// <summary>答案解析 docx。</summary>
    public string AnswerDocxPath { get; init; } = string.Empty;

    /// <summary>输出 pptx 路径。</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>参考模板 pptx（可选，仅用于提取背景图）。</summary>
    public string? TemplatePath { get; init; }

    /// <summary>自定义背景图（可选）；为空时使用引擎自带的默认背景。</summary>
    public string? BackgroundImagePath { get; init; }

    /// <summary>不生成阅读材料页。</summary>
    public bool SkipMaterial { get; init; }

    /// <summary>不生成【导语】页。</summary>
    public bool SkipGuide { get; init; }

    /// <summary>答案面板里不写【解析】，只留【答案】。</summary>
    public bool SkipAnalysis { get; init; }

    /// <summary>题目页完全不出现答案面板。</summary>
    public bool SkipAnswer { get; init; }

    /// <summary>只导出解析结构 JSON，不生成 pptx。</summary>
    public bool DumpJsonOnly { get; init; }

    /// <summary>解析结构 JSON 的输出路径（与 <see cref="DumpJsonOnly"/> 搭配）。</summary>
    public string? DumpJsonPath { get; init; }
}

/// <summary>生成进度（阶段 1~4，百分比 0~100，附引擎原始输出行）。</summary>
public readonly record struct Exam2PptProgress(int StageIndex, double Percent, string Message);

/// <summary>生成结果。</summary>
public sealed class Exam2PptResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }

    /// <summary>产物路径（成功时）。</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>幻灯片页数（成功时解析「共 N 页幻灯片」）。</summary>
    public int SlideCount { get; init; }

    /// <summary>未匹配到答案/解析的题号。</summary>
    public IReadOnlyList<string> MissingQuestions { get; init; } = Array.Empty<string>();

    /// <summary>失败原因（失败时）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>引擎全部输出行（含 stdout 与 stderr）。</summary>
    public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

    /// <summary>引擎进程退出码。</summary>
    public int ExitCode { get; init; }
}

/// <summary>
/// 评讲课件生成服务：把内嵌的 Python 引擎（exam2ppt）解压到用户目录并以子进程方式调用。
/// </summary>
public interface IExam2PptService
{
    /// <summary>引擎载荷版本（变更载荷内容时递增）。</summary>
    string PayloadVersion { get; }

    /// <summary>引擎解压后的根目录（运行时填充）。</summary>
    string EngineRoot { get; }

    /// <summary>确保引擎已就绪（首次调用会解压内嵌载荷）。</summary>
    Task<bool> EnsureEngineAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>生成讲评课件（内部会先确保引擎就绪）。</summary>
    Task<Exam2PptResult> GenerateAsync(
        Exam2PptOptions options,
        IProgress<Exam2PptProgress>? progress,
        CancellationToken ct);

    /// <summary>最近一次引擎准备失败的原因（无失败为 null）。</summary>
    string? LastEngineError { get; }
}
