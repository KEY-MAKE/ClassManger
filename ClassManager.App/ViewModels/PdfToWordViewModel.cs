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

    /// <summary>是否使用 MinerU 高质量解析（需要先装，未装时自动退回 pdf2docx）。</summary>
    [ObservableProperty]
    private bool _useHighQuality;

    /// <summary>高质量引擎是否就绪。</summary>
    [ObservableProperty]
    private bool _isMineruReady;

    /// <summary>高质量引擎的状态说明。</summary>
    [ObservableProperty]
    private string _mineruStatusText = "高质量模式未安装";

    /// <summary>是否正在下载高质量引擎。</summary>
    [ObservableProperty]
    private bool _isDownloadingMineru;

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

    /// <summary>刷新高质量引擎的可用状态。</summary>
    private void RefreshMineruState()
    {
        try
        {
            IsMineruReady = AppServices.Mineru.IsInstalled;

            if (IsMineruReady)
            {
                MineruStatusText = "高质量模式已就绪（MinerU，表格/公式更准）";
                UseHighQuality = true;
            }
            else
            {
                MineruStatusText = "高质量模式未安装（约 0.8GB，下载后可显著提升表格与公式的还原度）";
                UseHighQuality = false;
            }
        }
        catch
        {
            IsMineruReady = false;
            UseHighQuality = false;
        }

        OnPropertyChanged(nameof(CanDownloadMineru));
        OnPropertyChanged(nameof(CanUseHighQuality));
    }

    public bool CanDownloadMineru => !IsDownloadingMineru && !IsBusy;

    public bool CanUseHighQuality => IsMineruReady && !IsBusy;

    partial void OnIsMineruReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadMineru));
        OnPropertyChanged(nameof(CanUseHighQuality));
    }

    partial void OnIsDownloadingMineruChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadMineru));
        OnPropertyChanged(nameof(CanUseHighQuality));
    }

    /// <summary>
    /// 预下载高质量引擎。老师不急着用的时候可以放着慢慢下，装完一直有效。
    /// </summary>
    [RelayCommand]
    private async Task DownloadMineruAsync()
    {
        if (!CanDownloadMineru)
        {
            return;
        }

        IsDownloadingMineru = true;
        StatusText = "正在安装高质量引擎…";

        var progress = new Progress<string>(message =>
        {
            StatusText = message;
            AppendLog(message);
        });

        try
        {
            AppendLog("开始安装高质量引擎（MinerU + basic 模型，约 0.8GB）…");
            AppendLog("这个过程可能要十几分钟，可以放着不管，装完会用一次提示告诉你。");

            var result = await AppServices.Mineru
                .InstallAsync(progress)
                .ConfigureAwait(true);

            AppendLog((result.Ok ? "✓ " : "✗ ") + result.Message);

            RefreshMineruState();

            StatusText = result.Ok ? "高质量模式已就绪" : "安装没完成，可以稍后再试";
        }
        catch (Exception ex)
        {
            AppendLog("✗ 安装出错：" + ex.Message);
            StatusText = "安装出错";
        }
        finally
        {
            IsDownloadingMineru = false;
            RefreshMineruState();
        }
    }

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
            // 勾了高质量且引擎就绪 → 走 MinerU（表格/公式/多栏还原明显更好）
            var usedHighQuality = UseHighQuality && IsMineruReady
                                  && await TryConvertWithMineruAsync(target, progress).ConfigureAwait(true);

            if (usedHighQuality)
            {
                OutputFileText = target;
                StatusText = "转换完成（高质量模式）";
                AppendLog("✓ 已用 MinerU 生成：" + target);

                if (UseAiCleanup)
                {
                    await CleanupWithAiAsync(PdfPath, target).ConfigureAwait(true);
                }

                return;
            }

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
                await CleanupWithAiAsync(PdfPath, produced).ConfigureAwait(true);
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
    /// 用 MinerU 解析并写成 docx。失败返回 false（调用方自动回退到 pdf2docx）。
    /// </summary>
    private async Task<bool> TryConvertWithMineruAsync(string target, IProgress<string> progress)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "cm-mineru-" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            AppendLog("使用高质量模式（MinerU）解析…");

            var markdown = await AppServices.Mineru
                .ConvertToMarkdownAsync(PdfPath, tempDir, progress)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(markdown))
            {
                AppendLog("MinerU 解析失败，改用常规转换：" + (AppServices.Mineru.LastError ?? "未知原因"));
                return false;
            }

            AppendLog($"MinerU 解析完成（{markdown.Length} 字），正在生成 Word…");

            var engineReady = await AppServices.Exam2Ppt
                .EnsureEngineAsync(null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!engineReady)
            {
                AppendLog("文档引擎不可用，改用常规转换");
                return false;
            }

            await OfficeWriter
                .WriteDocxAsync(AppServices.Exam2Ppt.EngineRoot, markdown, target, CancellationToken.None)
                .ConfigureAwait(true);

            if (!File.Exists(target))
            {
                AppendLog("MinerU 结果写文件失败，改用常规转换");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppendLog("MinerU 出错，改用常规转换：" + ex.Message);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // 临时目录删不掉无所谓
            }
        }
    }

    /// <summary>
    /// 用 AI 校对转换结果。
    /// <para>
    /// <b>不再套用《试卷格式规范》</b> —— PDF 转 Word 不一定是为了做课件，
    /// 也不一定转的是试卷。规范只管评讲课件那条线。
    /// </para>
    /// <para>
    /// 这里做的事：拿 PDF 原本的文本当基准，和 pdf2docx 的输出逐段对比，
    /// 修掉乱码、合并过头/拆断的段落、丢失的字，另存为「原名_AI校对.docx」。
    /// </para>
    /// </summary>
    private async Task CleanupWithAiAsync(string pdfPath, string docxPath)
    {
        try
        {
            StatusText = "正在用 AI 校对排版…";
            AppendLog("开始 AI 校对（拿原 PDF 文本做基准，修排版和乱码）…");

            // 1) 需要能跑 PyMuPDF 的 Python
            var python = await PythonBootstrap.EnsureAsync(null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!python.Ok || python.PythonPath is null)
            {
                AppendLog("✗ 没有可用的 Python，跳过 AI 校对：" + python.Message);
                return;
            }

            // 2) 提取原 PDF 的纯文本
            AppendLog("正在提取原 PDF 文本…");
            var original = await _service
                .ExtractPdfTextAsync(pdfPath, python.PythonPath, CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(original))
            {
                AppendLog("✗ 提取不到 PDF 文本（可能是扫描件，没有文本层），跳过 AI 校对");
                StatusText = "扫描件无法校对（没有文本层）";
                return;
            }

            AppendLog($"原 PDF 提取到 {original.Length} 字");

            // 3) 读转换后的 docx 正文
            var read = AiDocumentReader.Read(docxPath);

            if (!read.Ok || string.IsNullOrWhiteSpace(read.Text))
            {
                AppendLog("✗ 读不出转换后的正文，跳过校对：" + read.Text);
                return;
            }

            AppendLog($"转换后正文 {read.Text.Length} 字");

            // 4) 让模型对比修正
            var history = new List<AiMessage>
            {
                new() { Role = "user", Content = BuildComparePrompt(original, read.Text) },
            };

            var reply = await AppServices.Ai
                .CompleteAsync(history, Array.Empty<AiToolSpec>(), null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!reply.Ok || string.IsNullOrWhiteSpace(reply.Content))
            {
                var why = reply.ReasoningOnly
                    ? "模型只输出了思考过程、没给正文。原稿已保留，稍后重试即可。"
                    : (reply.Error ?? "模型没有返回内容");

                AppendLog("✗ AI 校对失败：" + why);
                StatusText = "AI 校对没成功（原稿已生成）";
                return;
            }

            AppendLog($"AI 返回 {reply.Content.Length} 字，正在生成校对版…");

            // 5) 写回（另存，不覆盖原稿）
            var engineReady = await AppServices.Exam2Ppt
                .EnsureEngineAsync(null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!engineReady)
            {
                AppendLog("✗ 文档引擎不可用，AI 校对结果未写成 docx");
                return;
            }

            var cleanedPath = Path.Combine(
                Path.GetDirectoryName(docxPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(docxPath) + "_AI校对.docx");

            var write = await OfficeWriter
                .WriteDocxAsync(AppServices.Exam2Ppt.EngineRoot, reply.Content, cleanedPath, CancellationToken.None)
                .ConfigureAwait(true);

            if (File.Exists(cleanedPath))
            {
                OutputFileText = cleanedPath;
                StatusText = "转换 + AI 校对完成";
                AppendLog("✓ 已生成校对版：" + cleanedPath);
                AppendLog("（原稿保留在同目录，文件名不带 _AI校对）");
            }
            else
            {
                AppendLog("✗ 写文档失败：" + write);
            }
        }
        catch (Exception ex)
        {
            AppendLog("✗ AI 校对出错：" + ex.Message);
        }
    }

    /// <summary>校对提示词：以原 PDF 文本为基准，修转换稿的排版和乱码。</summary>
    private static string BuildComparePrompt(string original, string converted)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("你是文档校对助手。下面两份内容来自同一个 PDF：");
        sb.AppendLine();
        sb.AppendLine("【原件】用 PDF 阅读器直接提取的纯文本（按页，保留原始换行，可能有页眉页脚）");
        sb.AppendLine("【转换稿】pdf2docx 转成 Word 后的正文（常见问题：段落合并过头、拆断、乱码、丢字）");
        sb.AppendLine();
        sb.AppendLine("任务：**以【原件】为准**，修正【转换稿】，输出一份干净、可直接编辑的正文。");
        sb.AppendLine();
        sb.AppendLine("规则：");
        sb.AppendLine("1. **内容必须与原件一致** —— 不能增、不能删、不能改写题目和数字；");
        sb.AppendLine("2. **修乱码**：Ã、ï¿½、□、★、重复字符、明显不该出现的符号；");
        sb.AppendLine("3. **还原段落**：原件里一行一行的短行，合并成语义完整的段落；");
        sb.AppendLine("   原文有明显编号（一、二、1. 2.）、缩进或空行的，按它原本的层级分段；");
        sb.AppendLine("4. **不要套用任何「试卷格式规范」** —— 这份文档可能不是试卷，");
        sb.AppendLine("   按它本来的结构整理就行，不要硬套题号/选项格式；");
        sb.AppendLine("5. 用 Markdown 输出：标题用 `#`，普通段落直接写，列表用 `-`；");
        sb.AppendLine("6. 只输出整理后的正文，不要解释、不要前后缀、不要代码块标记。");
        sb.AppendLine();
        sb.AppendLine("===== 【原件】 =====");
        sb.AppendLine(Truncate(original, 24000));
        sb.AppendLine();
        sb.AppendLine("===== 【转换稿】 =====");
        sb.AppendLine(Truncate(converted, 24000));

        return sb.ToString();
    }

    /// <summary>截断超长文本，避免一次塞爆上下文。</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n…（后略）";

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
