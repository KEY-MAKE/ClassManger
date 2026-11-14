using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

            OutputFileText = result.OutputPath ?? target;
            StatusText = "转换完成";
            AppendLog("✓ " + result.Message);

            if (result.EngineInstalledNow)
            {
                AppendLog("（这次顺带装了转换引擎，下次转换会快很多）");
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
