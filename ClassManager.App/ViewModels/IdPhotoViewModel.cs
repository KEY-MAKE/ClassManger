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

    /// <summary>批量待处理的照片（第一张同时作为预览）。</summary>
    public ObservableCollection<string> SourceImages { get; } = new();

    /// <summary>批量摘要（显示在按钮下方）。</summary>
    [ObservableProperty]
    private string _batchSummary = string.Empty;

    [RelayCommand]
    private void BrowseImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择人物照片（可多选，批量换底）",
            Filter = "图片 (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        var last = AppServices.Settings.Current.LastImportDirectory;

        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
        {
            dialog.InitialDirectory = last;
        }

        if (dialog.ShowDialog() == true)
        {
            SetSources(dialog.FileNames);
        }
    }

    /// <summary>批量载入（多选 / 拖拽多张）。</summary>
    public void SetSources(IEnumerable<string> paths)
    {
        SourceImages.Clear();

        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && !SourceImages.Contains(path))
            {
                SourceImages.Add(path);
            }
        }

        Results.Clear();
        SelectedResult = null;
        Logs.Clear();

        if (SourceImages.Count == 0)
        {
            BatchSummary = string.Empty;
            return;
        }

        SourceImagePath = SourceImages[0];
        LoadPreview(SourceImages[0]);

        BatchSummary = SourceImages.Count == 1
            ? Path.GetFileName(SourceImages[0])
            : $"已选 {SourceImages.Count} 张：{Path.GetFileName(SourceImages[0])} 等";

        RaiseCommandState();
    }

    /// <summary>清空待处理列表。</summary>
    [RelayCommand]
    private void ClearSources()
    {
        SourceImages.Clear();
        SourceImagePath = string.Empty;
        PreviewImage = null;
        HasPreview = false;
        Results.Clear();
        SelectedResult = null;
        BatchSummary = string.Empty;
        RaiseCommandState();
    }

    /// <summary>供拖拽调用。</summary>
    public void SetSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        SourceImages.Clear();
        SourceImages.Add(path);
        BatchSummary = Path.GetFileName(path);

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

    private static void Diag(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "cm_idphoto.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n", new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!CanStart)
        {
            Diag($"CanStart=false，跳过（busy={IsBusy}, engineInstalling={IsInstallingEngine}）");
            return;
        }

        Diag("GenerateAsync 开始");

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

            // 批量：优先用多选列表；只有单张时退化成单元素列表
            var targets = SourceImages.Count > 0
                ? SourceImages.ToList()
                : (string.IsNullOrWhiteSpace(SourceImagePath) ? new List<string>() : new List<string> { SourceImagePath });

            if (targets.Count == 0)
            {
                StatusText = "请先选择照片";
                IsBusy = false;
                return;
            }

            var okCount = 0;
            var failCount = 0;

            for (var index = 0; index < targets.Count; index++)
            {
                var source = targets[index];
                var tag = targets.Count > 1 ? $"[{index + 1}/{targets.Count}] " : string.Empty;

                AppendLog($"{tag}—— {Path.GetFileName(source)} ——");
                StatusText = $"{tag}正在处理 {Path.GetFileName(source)}…";

                var result = await _service
                    .GenerateAsync(source, OutputDirectory, spec, progress, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!result.Ok)
                {
                    failCount++;
                    AppendLog($"{tag}✗ 失败：{result.Message}");
                    continue;
                }

                okCount++;

                foreach (var path in result.OutputPaths)
                {
                    Results.Add(path);
                    AppendLog($"{tag}· " + Path.GetFileName(path));
                }

                SelectedResult = Results.FirstOrDefault();
                OnPropertyChanged(nameof(HasResult));
            }

            StatusText = failCount == 0
                ? $"全部完成（{okCount} 张照片）"
                : $"完成 {okCount} 张，失败 {failCount} 张";

            AppendLog($"✓ 共生成 {Results.Count} 个文件（{okCount} 成功 / {failCount} 失败）");

            RefreshEngineState();

            try
            {
                AppServices.Settings.Current.LastExportDirectory = OutputDirectory;
                AppServices.Settings.Current.LastImportDirectory =
                    Path.GetDirectoryName(targets[0]) ?? AppServices.Settings.Current.LastImportDirectory;
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
            Diag("EX " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            Diag("GenerateAsync 结束");
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

    /// <summary>切到一寸（同时取消二寸，两者互斥）。</summary>
    [RelayCommand]
    private void UseOneInch()
    {
        IsOneInch = true;
        IsTwoInch = false;
    }

    /// <summary>切到二寸（同时取消一寸）。</summary>
    [RelayCommand]
    private void UseTwoInch()
    {
        IsTwoInch = true;
        IsOneInch = false;
    }

    /// <summary>
    /// 精细抠图。默认开 —— 边缘明显更干净，代价是慢一些。
    /// </summary>
    [ObservableProperty]
    private bool _isFineMatting = true;

    /// <summary>当前精度档的说明文字。</summary>
    public string MattingHint => IsFineMatting
        ? "当前：精细模式（ISNet + 边缘精修），单张约 10~20 秒。"
        : "当前：标准模式（U2Net），单张约 4 秒，边缘一般。";

    partial void OnIsFineMattingChanged(bool value) => OnPropertyChanged(nameof(MattingHint));

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

        return spec with
        {
            Backgrounds = backgrounds,
            // 完整保留用户上传的构图，只换底、不裁切
            KeepWholePerson = true,
            Matting = IsFineMatting ? IdPhotoMatting.Fine : IdPhotoMatting.Standard,
        };
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
