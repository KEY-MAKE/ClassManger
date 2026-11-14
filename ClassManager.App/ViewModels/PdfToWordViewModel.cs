using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using ClassManager.App.Models;
using ClassManager.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClassManager.App.ViewModels;

/// <summary>
/// PDF 转 Word 页。
/// <para>
/// 转出来的是「能编辑的草稿」：pdf2docx 按页面坐标还原段落和表格，
/// 版式接近原样但不是逐像素复制。要更整齐可以接着让 AI 办公重排。
/// </para>
/// </summary>
public sealed partial class PdfToWordViewModel : ObservableObject
{
    private readonly PdfToWordService _service = new();

    public PdfToWordViewModel()
    {
        var last = AppServices.Settings.Current.LastExportDirectory;

        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
        {
            OutputDirectory = last;
        }
        else
        {
            OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    [ObservableProperty]
    private string _pdfPath = string.Empty;

    /// <summary>输出目录（默认文档目录，可改）。</summary>
    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private string _statusText = "选一份 PDF，点「开始转换」";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _outputFileText = string.Empty;

    /// <summary>
    /// 转换后是否让 AI 按《试卷格式规范》重新分段。
    /// <para>
    /// 为什么需要：pdf2docx 按坐标聚类，会把「标题+副标题」「题干+选项」这类
    /// 视觉上连续的行合并成一个段落。原样打开能看，但没法继续排版。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _useAiCleanup = true;

    /// <summary>转换过程日志（用户能看懂的那种）。</summary>
    public ObservableCollection<string> Logs { get; } = new();

    public bool CanStart => !IsBusy && !string.IsNullOrWhiteSpace(PdfPath) && File.Exists(PdfPath);

    public bool HasResult => !string.IsNullOrWhiteSpace(OutputFileText);

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(HasResult));
    }

    partial void OnPdfPathChanged(string value) => OnPropertyChanged(nameof(CanStart));

    partial void OnOutputFileTextChanged(string value) => OnPropertyChanged(nameof(HasResult));

    [RelayCommand]
    private void BrowsePdf()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            CheckFileExists = true,
        };

        var last = AppServices.Settings.Current.LastImportDirectory;

        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
        {
            dialog.InitialDirectory = last;
        }

        if (dialog.ShowDialog() == true)
        {
            SetPdf(dialog.FileName);
        }
    }

    /// <summary>供拖拽调用。</summary>
    public void SetPdf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        PdfPath = path;
        OutputFileText = string.Empty;
        Logs.Clear();

        var name = Path.GetFileNameWithoutExtension(path);
        StatusText = $"已选择：{Path.GetFileName(path)}";
        AppendLog($"源文件：{path}");
        AppendLog($"将输出到：{Path.Combine(OutputDirectory, name + ".docx")}");
        AppendLog("点「开始转换」后首次会先安装转换引擎（约 50MB，1–3 分钟）。");

        OnPropertyChanged(nameof(CanStart));
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择输出目录",
        };

        if (Directory.Exists(OutputDirectory))
        {
            dialog.InitialDirectory = OutputDirectory;
        }

        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (!CanStart)
        {
            return;
        }

        IsBusy = true;
        OutputFileText = string.Empty;
        Logs.Clear();

        var target = Path.Combine(
            OutputDirectory,
            Path.GetFileNameWithoutExtension(PdfPath) + ".docx");

        var progress = new Progress<string>(message =>
        {
            StatusText = message;
            AppendLog(message);
        });

        try
        {
            var result = await _service.ConvertAsync(PdfPath, target, progress).ConfigureAwait(true);

            if (!result.Ok)
            {
                StatusText = "转换失败";
                AppendLog("✗ " + result.Message);
                return;
            }

            var produced = result.OutputPath ?? target;
            OutputFileText = produced;
            StatusText = "转换完成";
            AppendLog("✓ " + result.Message);

            if (result.EngineInstalledNow)
            {
                AppendLog("（这次顺带装了转换引擎，下次转换会快很多）");
            }

            // ③ AI 整理段落结构（默认开）
            if (UseAiCleanup)
            {
                await CleanupWithAiAsync(produced).ConfigureAwait(true);
            }

            try
            {
                AppServices.Settings.Current.LastExportDirectory = OutputDirectory;
                AppServices.Settings.Save();
            }
            catch
            {
                // 忽略
            }
        }
        catch (Exception ex)
        {
            StatusText = "转换出错";
            AppendLog("✗ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenResult()
    {
        if (!HasResult)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(OutputFileText) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打不开文件：" + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            var dir = Directory.Exists(OutputDirectory)
                ? OutputDirectory
                : Path.GetDirectoryName(OutputFileText);

            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 用 AI 按《试卷格式规范》重新分段，另存为「原名_AI整理.docx」。
    /// <para>不覆盖原始转换结果 —— 万一 AI 整理得不如原样，用户还有原稿。</para>
    /// </summary>
    private async Task CleanupWithAiAsync(string docxPath)
    {
        try
        {
            StatusText = "正在用 AI 整理段落结构…";
            AppendLog("开始 AI 整理（按《试卷格式规范》重排段落）…");

            // 1) 读出转换后的正文
            var read = AiDocumentReader.Read(docxPath);

            if (!read.Ok || string.IsNullOrWhiteSpace(read.Text))
            {
                AppendLog("✗ 读不出文档正文，跳过 AI 整理：" + read.Text);
                return;
            }

            AppendLog($"已读取正文 {read.Text.Length} 字");

            // 2) 取内置规范（工作区里那份，由 AI 办公页释放）
            var spec = ReadExamSpec();

            if (string.IsNullOrWhiteSpace(spec))
            {
                AppendLog("（没找到《试卷格式规范.md》，按通用规则整理）");
            }

            // 3) 调模型
            var history = new List<AiMessage>
            {
                new() { Role = "system", Content = BuildCleanupPrompt(spec) },
                new() { Role = "user", Content = read.Text },
            };

            var reply = await AppServices.Ai
                .CompleteAsync(history, Array.Empty<AiToolSpec>(), null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!reply.Ok || string.IsNullOrWhiteSpace(reply.Content))
            {
                AppendLog("✗ AI 整理失败：" + (reply.Error ?? "模型没有返回内容"));
                return;
            }

            AppendLog($"AI 返回 {reply.Content.Length} 字，正在生成新文档…");

            // 4) 写回（另存，不覆盖原稿）
            var engineRoot = AppServices.Exam2Ppt.EngineRoot;
            var ok = await AppServices.Exam2Ppt
                .EnsureEngineAsync(null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!ok)
            {
                AppendLog("✗ 文档引擎不可用，AI 整理结果未写成 docx");
                return;
            }

            var cleanedPath = Path.Combine(
                Path.GetDirectoryName(docxPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(docxPath) + "_AI整理.docx");

            var write = await OfficeWriter
                .WriteDocxAsync(engineRoot, reply.Content, cleanedPath, CancellationToken.None)
                .ConfigureAwait(true);

            if (File.Exists(cleanedPath))
            {
                OutputFileText = cleanedPath;
                StatusText = "转换 + AI 整理完成";
                AppendLog("✓ 已生成整理版：" + cleanedPath);
                AppendLog("（原稿保留在同目录，文件名不带 _AI整理）");
            }
            else
            {
                AppendLog("✗ 写文档失败：" + write);
            }
        }
        catch (Exception ex)
        {
            AppendLog("✗ AI 整理出错：" + ex.Message);
        }
    }

    /// <summary>读工作区里的《试卷格式规范.md》。</summary>
    private static string ReadExamSpec()
    {
        try
        {
            var workspace = AppServices.AiStore.LoadSettings().WorkspaceDirectory;

            if (string.IsNullOrWhiteSpace(workspace))
            {
                return string.Empty;
            }

            var path = Path.Combine(workspace, "试卷格式规范.md");

            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>整理提示词。</summary>
    private static string BuildCleanupPrompt(string spec)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("你是试卷排版助手。下面这份文本是从 PDF 转换来的 Word 正文，");
        sb.AppendLine("转换工具按视觉位置聚类，把多行合并成了同一个段落。");
        sb.AppendLine();
        sb.AppendLine("任务：重新分段，还原成一份结构清晰的试卷。要求：");
        sb.AppendLine("1. 每个大题的题干（如「一、单选题」）单独成段，用 `## ` 开头；");
        sb.AppendLine("2. 每个小题单独成段，题号与题干在同一行；");
        sb.AppendLine("3. 选项 A/B/C/D 单独成行或用列表，不要挤在题干里；");
        sb.AppendLine("4. **不要改写、不要增删任何题目内容和数字** —— 你只负责分段和层级；");
        sb.AppendLine("5. 用 Markdown 输出：`##` 大题、普通段落小题、`-` 选项。");
        sb.AppendLine("6. 只输出 Markdown 正文，不要任何解释、前后缀或代码块标记。");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(spec))
        {
            sb.AppendLine("下面是这份试卷必须遵守的格式规范（用户提供的标准），请严格对齐：");
            sb.AppendLine();
            sb.AppendLine(spec);
        }

        return sb.ToString();
    }

    private void AppendLog(string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Logs.Add($"[{stamp}] {message}");

        // 只留最近 200 条，避免长时间运行把界面拖慢
        while (Logs.Count > 200)
        {
            Logs.RemoveAt(0);
        }
    }
}
