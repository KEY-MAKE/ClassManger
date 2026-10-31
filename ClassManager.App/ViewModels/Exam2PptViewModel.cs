using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Text;
using ClassManager.App.Services;
using ClassManager.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClassManager.App.ViewModels;

/// <summary>
/// 讲评课件页 ViewModel：选择试题/答案 → 调用内嵌引擎生成讲评课件 pptx，实时展示进度与引擎日志。
/// </summary>
public sealed partial class Exam2PptViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly IExam2PptService _engine;
    private readonly Exam2PptSettingsStore _settingsStore;
    private readonly Exam2PptSettings _settings;
    private readonly StringBuilder _log = new();

    private CancellationTokenSource? _cts;

    public Exam2PptViewModel(IShell shell)
    {
        _shell = shell;
        _engine = AppServices.Exam2Ppt;
        _settingsStore = new Exam2PptSettingsStore();
        _settings = _settingsStore.Load();
        BackgroundImagePath = _settings.BackgroundImagePath;
        UpdateBackgroundText();
    }

    /// <summary>试题原卷 docx。</summary>
    [ObservableProperty]
    private string _examPath = string.Empty;

    /// <summary>答案解析 docx。</summary>
    [ObservableProperty]
    private string _answerPath = string.Empty;

    /// <summary>参考模板 pptx（可选）。</summary>
    [ObservableProperty]
    private string _templatePath = string.Empty;

    /// <summary>输出 pptx 路径。</summary>
    [ObservableProperty]
    private string _outputPath = string.Empty;

    /// <summary>生成阅读材料页。</summary>
    [ObservableProperty]
    private bool _generateMaterial = true;

    /// <summary>生成【导语】页。</summary>
    [ObservableProperty]
    private bool _generateGuide = true;

    /// <summary>答案面板写【解析】。</summary>
    [ObservableProperty]
    private bool _writeAnalysis = true;

    /// <summary>题目页显示答案面板。</summary>
    [ObservableProperty]
    private bool _showAnswerPanel = true;

    /// <summary>完成后自动打开课件。</summary>
    [ObservableProperty]
    private bool _openAfterDone = true;

    /// <summary>是否正在生成。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>进度百分比。</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>阶段文字。</summary>
    [ObservableProperty]
    private string _stageText = "就绪";

    /// <summary>自定义背景图路径（空 = 默认背景）。</summary>
    [ObservableProperty]
    private string _backgroundImagePath = string.Empty;

    /// <summary>背景的简短描述。</summary>
    [ObservableProperty]
    private string _backgroundText = "背景：默认";

    /// <summary>引擎日志。</summary>
    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>开始按钮是否可用。</summary>
    public bool CanStart => !IsBusy;

    /// <summary>取消按钮是否可用。</summary>
    public bool CanCancel => IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnExamPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var suggested = SuggestOutputPath(value);
            if (string.IsNullOrWhiteSpace(OutputPath) || LooksLikeSuggested(OutputPath))
            {
                OutputPath = suggested;
            }
        }
    }

    [RelayCommand]
    private void BrowseExam()
    {
        var path = PickDocx("选择试题原卷（Word）", ExamPath);
        if (path is not null)
        {
            ExamPath = path;
        }
    }

    [RelayCommand]
    private void BrowseAnswer()
    {
        var path = PickDocx("选择答案解析（Word）", AnswerPath);
        if (path is not null)
        {
            AnswerPath = path;
        }
    }

    [RelayCommand]
    private void BrowseTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择参考模板（可选，仅用于提取背景图）",
            Filter = "PowerPoint 演示文稿 (*.pptx)|*.pptx",
        };

        if (Directory.Exists(Path.GetDirectoryName(TemplatePath)))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(TemplatePath);
        }

        if (dialog.ShowDialog() == true)
        {
            TemplatePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择课件输出位置",
            Filter = "PowerPoint 演示文稿 (*.pptx)|*.pptx",
            DefaultExt = ".pptx",
            FileName = string.IsNullOrWhiteSpace(OutputPath)
                ? "（讲评课件）.pptx"
                : Path.GetFileName(OutputPath),
        };

        var dir = Path.GetDirectoryName(OutputPath);
        if (Directory.Exists(dir))
        {
            dialog.InitialDirectory = dir;
        }

        if (dialog.ShowDialog() == true)
        {
            OutputPath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void ClearExam() => ExamPath = string.Empty;

    [RelayCommand]
    private void ClearAnswer() => AnswerPath = string.Empty;

    [RelayCommand]
    private void ClearTemplate() => TemplatePath = string.Empty;

    /// <summary>打开背景设置窗口（预览 + 选择图片 / 使用默认背景）。</summary>
    [RelayCommand]
    private async Task ConfigureBackgroundAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // 默认背景在引擎目录里，先确保引擎已解压，预览才有东西可看
        await _engine.EnsureEngineAsync(null, CancellationToken.None).ConfigureAwait(true);

        var defaultBackground = Path.Combine(_engine.EngineRoot, "assets", "slide_bg.png");
        if (!File.Exists(defaultBackground))
        {
            defaultBackground = string.Empty;
        }

        var window = new BackgroundSettingsWindow(
            string.IsNullOrWhiteSpace(BackgroundImagePath) ? null : BackgroundImagePath,
            string.IsNullOrEmpty(defaultBackground) ? null : defaultBackground)
        {
            Owner = Application.Current?.MainWindow,
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        BackgroundImagePath = window.SelectedPath ?? string.Empty;
        _settings.BackgroundImagePath = BackgroundImagePath;
        _settingsStore.Save(_settings);
        UpdateBackgroundText();
        _shell.ShowToast("背景已设置", BackgroundText, ControlAppearance.Success);
    }

    private void UpdateBackgroundText()
    {
        BackgroundText = string.IsNullOrWhiteSpace(BackgroundImagePath)
            ? "背景：默认（引擎自带）"
            : $"背景：{Path.GetFileName(BackgroundImagePath)}";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!ValidateInputs(out var error))
        {
            _shell.ShowToast("无法开始", error, ControlAppearance.Caution);
            return;
        }

        _log.Clear();
        LogText = string.Empty;
        ProgressPercent = 3;
        StageText = "正在准备引擎…";
        IsBusy = true;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var progress = new Progress<Exam2PptProgress>(p =>
        {
            ProgressPercent = p.Percent;
            if (!string.IsNullOrWhiteSpace(p.Message))
            {
                StageText = DescribeStage(p.StageIndex, p.Message);
            }
        });

        try
        {
            var options = new Exam2PptOptions
            {
                ExamDocxPath = ExamPath,
                AnswerDocxPath = AnswerPath,
                OutputPath = OutputPath,
                TemplatePath = string.IsNullOrWhiteSpace(TemplatePath) ? null : TemplatePath,
                BackgroundImagePath = string.IsNullOrWhiteSpace(BackgroundImagePath) ? null : BackgroundImagePath,
                SkipMaterial = !GenerateMaterial,
                SkipGuide = !GenerateGuide,
                SkipAnalysis = !WriteAnalysis,
                SkipAnswer = !ShowAnswerPanel,
            };

            var result = await _engine.GenerateAsync(options, progress, token).ConfigureAwait(true);

            AppendLogLines(result.LogLines);

            if (result.Ok)
            {
                ProgressPercent = 100;
                StageText = $"完成：共 {result.SlideCount} 页";

                var missingNote = result.MissingQuestions.Count > 0
                    ? $"，{result.MissingQuestions.Count} 题未匹配到答案/解析"
                    : string.Empty;

                _shell.SetStatus($"讲评课件已生成：{Path.GetFileName(result.OutputPath)}");
                _shell.ShowToast(
                    "生成成功",
                    $"共 {result.SlideCount} 页幻灯片{missingNote}",
                    ControlAppearance.Success);

                if (OpenAfterDone && File.Exists(result.OutputPath))
                {
                    OpenWithShell(result.OutputPath);
                }
            }
            else
            {
                var engineNotReady = _engine.LastEngineError;
                var message = string.IsNullOrWhiteSpace(engineNotReady) ? result.Message : engineNotReady;
                StageText = "生成失败";
                _shell.SetStatus("讲评课件生成失败");
                _shell.ShowToast("生成失败", message, ControlAppearance.Danger);
                AppendLogLines(new[] { string.Empty, $"[失败] {message}" });
            }
        }
        catch (OperationCanceledException)
        {
            StageText = "已取消";
            AppendLogLines(new[] { "[已取消] 用户中断了生成。" });
        }
        catch (Exception ex)
        {
            StageText = "生成失败";
            AppendLogLines(new[] { string.Empty, $"[异常] {ex.Message}" });
            _shell.ShowToast("生成失败", ex.Message, ControlAppearance.Danger);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StageText = "正在取消…";
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var dir = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            OpenWithShell(dir);
            return;
        }

        _shell.ShowToast("提示", "输出目录还不存在，请先选择输出位置。", ControlAppearance.Caution);
    }

    [RelayCommand]
    private async Task PreviewStructureAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!ValidateInputs(out var error))
        {
            _shell.ShowToast("无法预览", error, ControlAppearance.Caution);
            return;
        }

        IsBusy = true;
        _log.Clear();
        LogText = string.Empty;
        StageText = "正在解析结构…";

        var jsonPath = Path.Combine(Path.GetTempPath(), $"cm_exam2ppt_{Guid.NewGuid():N}.json");
        var tempPptx = Path.Combine(Path.GetTempPath(), $"cm_exam2ppt_{Guid.NewGuid():N}.pptx");

        try
        {
            var options = new Exam2PptOptions
            {
                ExamDocxPath = ExamPath,
                AnswerDocxPath = AnswerPath,
                OutputPath = tempPptx,
                TemplatePath = string.IsNullOrWhiteSpace(TemplatePath) ? null : TemplatePath,
                BackgroundImagePath = string.IsNullOrWhiteSpace(BackgroundImagePath) ? null : BackgroundImagePath,
                SkipMaterial = !GenerateMaterial,
                SkipGuide = !GenerateGuide,
                SkipAnalysis = !WriteAnalysis,
                SkipAnswer = !ShowAnswerPanel,
                DumpJsonPath = jsonPath,
            };

            var result = await _engine.GenerateAsync(options, null, CancellationToken.None).ConfigureAwait(true);
            AppendLogLines(result.LogLines);

            if (result.Ok && File.Exists(jsonPath))
            {
                StageText = "解析结构已导出";
                OpenWithShell(jsonPath);
                _shell.ShowToast("预览", "已用默认程序打开解析结构 JSON。", ControlAppearance.Info);
            }
            else
            {
                StageText = "预览失败";
                _shell.ShowToast("预览失败", result.Message, ControlAppearance.Danger);
            }
        }
        catch (Exception ex)
        {
            StageText = "预览失败";
            _shell.ShowToast("预览失败", ex.Message, ControlAppearance.Danger);
        }
        finally
        {
            TryDelete(tempPptx);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowHelpAsync()
    {
        try
        {
            if (!await _engine.EnsureEngineAsync(null, CancellationToken.None).ConfigureAwait(true))
            {
                _shell.ShowToast("提示", _engine.LastEngineError ?? "引擎未就绪", ControlAppearance.Caution);
                return;
            }

            var helpPath = Path.Combine(_engine.EngineRoot, "使用说明.txt");
            if (File.Exists(helpPath))
            {
                OpenWithShell(helpPath);
                return;
            }

            var specPath = Path.Combine(_engine.EngineRoot, "试卷格式规范.md");
            if (File.Exists(specPath))
            {
                OpenWithShell(specPath);
                return;
            }

            _shell.ShowToast("提示", "未找到使用说明文件。", ControlAppearance.Caution);
        }
        catch (Exception ex)
        {
            _shell.ShowToast("提示", ex.Message, ControlAppearance.Caution);
        }
    }

    /// <summary>接收页面拖入的文件，按扩展名自动分配角色。</summary>
    public void ApplyDroppedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var docx = paths.Where(p => p.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)).ToList();
        var pptx = paths.FirstOrDefault(p => p.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase));

        // 两份 docx：先按文件名关键词猜角色，猜不出来就按拖入顺序
        foreach (var path in docx)
        {
            var name = Path.GetFileName(path);
            var looksLikeAnswer = name.Contains("答案", StringComparison.Ordinal)
                                  || name.Contains("解析", StringComparison.Ordinal);

            if (looksLikeAnswer)
            {
                AnswerPath = path;
            }
            else if (string.IsNullOrWhiteSpace(ExamPath))
            {
                ExamPath = path;
            }
            else if (string.IsNullOrWhiteSpace(AnswerPath))
            {
                AnswerPath = path;
            }
        }

        if (string.IsNullOrWhiteSpace(ExamPath) && docx.Count > 0)
        {
            ExamPath = docx[0];
        }

        if (string.IsNullOrWhiteSpace(AnswerPath) && docx.Count > 1)
        {
            AnswerPath = docx[1];
        }

        if (!string.IsNullOrWhiteSpace(pptx))
        {
            TemplatePath = pptx;
        }
    }

    private bool ValidateInputs(out string error)
    {
        if (string.IsNullOrWhiteSpace(ExamPath) || !File.Exists(ExamPath))
        {
            error = "请选择试题原卷（.docx）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AnswerPath) || !File.Exists(AnswerPath))
        {
            error = "请选择答案解析（.docx）。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(TemplatePath) && !File.Exists(TemplatePath))
        {
            error = "参考模板文件不存在。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            error = "请指定输出位置。";
            return false;
        }

        var dir = Path.GetDirectoryName(OutputPath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            error = "输出路径缺少目录。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            error = $"输出目录不可写：{ex.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string SuggestOutputPath(string examPath)
    {
        var dir = Path.GetDirectoryName(examPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(examPath);
        return Path.Combine(dir, $"{name}（讲评课件）.pptx");
    }

    private static bool LooksLikeSuggested(string path)
    {
        return Path.GetFileNameWithoutExtension(path).EndsWith("（讲评课件）", StringComparison.Ordinal);
    }

    private static string DescribeStage(int stage, string message)
    {
        // 优先展示引擎原始语义化行，其次回落到阶段名
        if (message.Contains("排版第", StringComparison.Ordinal))
        {
            return message.Trim();
        }

        return stage switch
        {
            1 => "① 读取解析…",
            2 => "② 读取试题原卷…",
            3 => "③ 准备版式素材…",
            4 => "④ 排版生成…",
            _ => message.Trim(),
        };
    }

    private void AppendLogLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        foreach (var line in lines)
        {
            _log.AppendLine(line);
        }

        LogText = _log.ToString();
    }

    private static string? PickDocx(string title, string current)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Word 文档 (*.docx)|*.docx",
        };

        var dir = Path.GetDirectoryName(current);
        if (Directory.Exists(dir))
        {
            dialog.InitialDirectory = dir;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 忽略：打开失败不影响主流程
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
            // 忽略
        }
    }
}
