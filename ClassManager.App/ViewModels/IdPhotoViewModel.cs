using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassManager.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClassManager.App.ViewModels;

/// <summary>
/// 证件照页。
/// <para>
/// 老师传一张人物照，选规格和底色，一键出标准证件照。
/// 第一次用要装图像处理组件并下载抠图模型，之后就一直是十几秒出图。
/// </para>
/// </summary>
public sealed partial class IdPhotoViewModel : ObservableObject
{
    private readonly IIdPhotoService _service = AppServices.IdPhoto;

    public IdPhotoViewModel()
    {
        var last = AppServices.Settings.Current.LastExportDirectory;

        OutputDirectory = !string.IsNullOrWhiteSpace(last) && Directory.Exists(last)
            ? last
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        RefreshEngineState();
    }

    /// <summary>源照片路径。</summary>
    [ObservableProperty]
    private string _sourceImagePath = string.Empty;

    /// <summary>缩略图预览。</summary>
    [ObservableProperty]
    private ImageSource? _previewImage;

    /// <summary>是否已有缩略图（控制预览占位文字的显隐）。</summary>
    [ObservableProperty]
    private bool _hasPreview;

    /// <summary>一寸（295×413）。</summary>
    [ObservableProperty]
    private bool _isOneInch = true;

    /// <summary>二寸（413×579）。</summary>
    [ObservableProperty]
    private bool _isTwoInch;

    /// <summary>红底。</summary>
    [ObservableProperty]
    private bool _redEnabled = true;

    /// <summary>蓝底。</summary>
    [ObservableProperty]
    private bool _blueEnabled = true;

    /// <summary>白底。</summary>
    [ObservableProperty]
    private bool _whiteEnabled = true;

    /// <summary>输出目录。</summary>
    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    /// <summary>状态文案。</summary>
    [ObservableProperty]
    private string _statusText = "选一张正面免冠照片，点「生成证件照」";

    /// <summary>是否正在生成。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>图像处理组件是否已就绪。</summary>
    [ObservableProperty]
    private bool _isEngineReady;

    /// <summary>组件状态说明。</summary>
    [ObservableProperty]
    private string _engineStatusText = "首次使用需安装图像处理组件";

    /// <summary>是否正在安装组件。</summary>
    [ObservableProperty]
    private bool _isInstallingEngine;

    /// <summary>结果列表里当前选中的一张（用于拼相纸）。</summary>
    [ObservableProperty]
    private string? _selectedResult;

    /// <summary>处理日志。</summary>
    public ObservableCollection<string> Logs { get; } = new();

    /// <summary>生成出来的证件照路径。</summary>
    public ObservableCollection<string> Results { get; } = new();

    public bool CanStart =>
        !IsBusy
        && !IsInstallingEngine
        && !string.IsNullOrWhiteSpace(SourceImagePath)
        && File.Exists(SourceImagePath)
        && HasBackgroundSelected;

    public bool HasBackgroundSelected => RedEnabled || BlueEnabled || WhiteEnabled;

    public bool HasResult => Results.Count > 0;

    public bool CanComposeSheet => !IsBusy && !IsInstallingEngine && SelectedResult is not null;

    public bool CanInstallEngine => !IsBusy && !IsInstallingEngine && !IsEngineReady;

    /// <summary>当前规格的说明（界面上的小字）。</summary>
    public string SpecSummary => IsTwoInch
        ? "二寸：35×49 mm，413×579 px（300 DPI）"
        : "一寸：25×35 mm，295×413 px（300 DPI）";

    partial void OnIsBusyChanged(bool value) => RaiseCommandState();

    partial void OnIsInstallingEngineChanged(bool value) => RaiseCommandState();

    partial void OnSourceImagePathChanged(string value) => RaiseCommandState();

    partial void OnRedEnabledChanged(bool value) => RaiseCommandState();

    partial void OnBlueEnabledChanged(bool value) => RaiseCommandState();

    partial void OnWhiteEnabledChanged(bool value) => RaiseCommandState();

    partial void OnSelectedResultChanged(string? value) =>
        OnPropertyChanged(nameof(CanComposeSheet));

    partial void OnIsEngineReadyChanged(bool value) => RaiseCommandState();

    partial void OnIsOneInchChanged(bool value)
    {
        if (value && IsTwoInch)
        {
            IsTwoInch = false;
        }

        OnPropertyChanged(nameof(SpecSummary));
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnIsTwoInchChanged(bool value)
    {
        if (value && IsOneInch)
        {
            IsOneInch = false;
        }

        OnPropertyChanged(nameof(SpecSummary));
        OnPropertyChanged(nameof(CanStart));
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanComposeSheet));
        OnPropertyChanged(nameof(CanInstallEngine));
        OnPropertyChanged(nameof(HasBackgroundSelected));
    }

    /// <summary>刷新组件状态（只读安装标记，不跑进程，不卡界面）。</summary>
    private void RefreshEngineState()
    {
        IsEngineReady = _service.IsReady;

        EngineStatusText = IsEngineReady
            ? "图像处理组件已就绪，可以开始生成"
            : "首次使用需安装图像处理组件（约 150MB，另下载 170MB 抠图模型）";
    }

    [RelayCommand]
    private void BrowseImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择人物照片",
            Filter = "图片 (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        var last = AppServices.Settings.Current.LastImportDirectory;

        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
        {
            dialog.InitialDirectory = last;
        }

        if (dialog.ShowDialog() == true)
        {
            SetSource(dialog.FileName);
        }
    }

    /// <summary>供拖拽调用。</summary>
    public void SetSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        SourceImagePath = path;
        Results.Clear();
        SelectedResult = null;
        Logs.Clear();

        LoadPreview(path);

        StatusText = "已选择：" + Path.GetFileName(path);
        AppendLog("源照片：" + path);
        AppendLog("将输出到：" + OutputDirectory);

        OnPropertyChanged(nameof(HasResult));
    }

    private void LoadPreview(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 420;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage = bitmap;
            HasPreview = true;
        }
        catch (Exception ex)
        {
            PreviewImage = null;
            HasPreview = false;
            AppendLog("✗ 预览加载失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog { Title = "选择输出目录" };

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
    private async Task InstallEngineAsync()
    {
        if (!CanInstallEngine)
        {
            return;
        }

        IsInstallingEngine = true;
        StatusText = "正在安装图像处理组件…";

        var progress = new Progress<string>(message =>
        {
            StatusText = message;
            AppendLog(message);
        });

        try
        {
            AppendLog("开始安装 rembg / onnxruntime（仅首次）…");

            var ok = await _service
                .EnsureDependenciesAsync(progress, CancellationToken.None)
                .ConfigureAwait(true);

            AppendLog(ok ? "✓ 组件安装完成" : "✗ 安装没完成：" + (_service.LastError ?? "未知原因"));
            StatusText = ok ? "图像处理组件已就绪" : "安装没完成，可以稍后再试";
        }
        catch (Exception ex)
        {
            AppendLog("✗ 安装出错：" + ex.Message);
            StatusText = "安装出错";
        }
        finally
        {
            IsInstallingEngine = false;
            RefreshEngineState();
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!CanStart)
        {
            return;
        }

        var spec = BuildSpec();

        IsBusy = true;
        Results.Clear();
        SelectedResult = null;
        Logs.Clear();
        OnPropertyChanged(nameof(HasResult));

        var progress = new Progress<string>(message =>
        {
            StatusText = message;
            AppendLog(message);
        });

        try
        {
            AppendLog($"规格：{spec.Name}（{spec.Width}×{spec.Height}），底色："
                     + string.Join("、", spec.Backgrounds.Select(IdPhotoSpec.SuffixOf)));

            var result = await _service
                .GenerateAsync(SourceImagePath, OutputDirectory, spec, progress, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Ok)
            {
                StatusText = "生成失败";
                AppendLog("✗ " + result.Message);
                return;
            }

            foreach (var path in result.OutputPaths)
            {
                Results.Add(path);
                AppendLog("· " + path);
            }

            SelectedResult = Results.FirstOrDefault();
            OnPropertyChanged(nameof(HasResult));

            StatusText = "生成完成";
            AppendLog("✓ " + result.Message);

            RefreshEngineState();

            try
            {
                AppServices.Settings.Current.LastExportDirectory = OutputDirectory;
                AppServices.Settings.Current.LastImportDirectory =
                    Path.GetDirectoryName(SourceImagePath) ?? AppServices.Settings.Current.LastImportDirectory;
                AppServices.Settings.Save();
            }
            catch
            {
                // 设置保存失败不影响出图
            }

            // 出完图直接把目录打开，老师不用再找一遍
            OpenFolder();
        }
        catch (Exception ex)
        {
            StatusText = "生成出错";
            AppendLog("✗ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ComposeSheetAsync()
    {
        var source = SelectedResult;

        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            return;
        }

        var spec = BuildSpec();
        var target = Path.Combine(
            Path.GetDirectoryName(source) ?? OutputDirectory,
            Path.GetFileNameWithoutExtension(source) + "_6寸排版.jpg");

        IsBusy = true;
        StatusText = "正在拼 6 寸相纸…";
        AppendLog("开始排版：" + Path.GetFileName(target));

        var progress = new Progress<string>(message =>
        {
            StatusText = message;
            AppendLog(message);
        });

        try
        {
            var output = await _service
                .ComposeSheetAsync(source, target, spec, progress, CancellationToken.None)
                .ConfigureAwait(true);

            if (output is null)
            {
                StatusText = "排版失败";
                AppendLog("✗ " + (_service.LastError ?? "未知原因"));
                return;
            }

            Results.Add(output);
            SelectedResult = output;
            OnPropertyChanged(nameof(HasResult));

            StatusText = "排版完成";
            AppendLog("✓ 已生成 6 寸相纸：" + output);
            OpenFolder();
        }
        catch (Exception ex)
        {
            StatusText = "排版出错";
            AppendLog("✗ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IdPhotoSpec BuildSpec()
    {
        var backgrounds = new List<IdPhotoBackground>();

        if (RedEnabled)
        {
            backgrounds.Add(IdPhotoBackground.Red);
        }

        if (BlueEnabled)
        {
            backgrounds.Add(IdPhotoBackground.Blue);
        }

        if (WhiteEnabled)
        {
            backgrounds.Add(IdPhotoBackground.White);
        }

        var spec = IsTwoInch ? IdPhotoSpec.TwoInch : IdPhotoSpec.OneInch;

        return spec with { Backgrounds = backgrounds };
    }

    [RelayCommand]
    private void OpenOutputFolder() => OpenFolder();

    private void OpenFolder()
    {
        try
        {
            var dir = !string.IsNullOrWhiteSpace(SelectedResult)
                ? Path.GetDirectoryName(SelectedResult)
                : OutputDirectory;

            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // 打不开资源管理器不算错误
        }
    }

    [RelayCommand]
    private void OpenResult()
    {
        if (string.IsNullOrWhiteSpace(SelectedResult) || !File.Exists(SelectedResult))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(SelectedResult) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打不开文件：" + ex.Message;
        }
    }

    private void AppendLog(string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Logs.Add($"[{stamp}] {message}");

        while (Logs.Count > 200)
        {
            Logs.RemoveAt(0);
        }
    }
}
