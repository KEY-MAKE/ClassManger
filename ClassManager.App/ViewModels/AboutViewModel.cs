using System.Threading.Tasks;
using System.Windows;
using ClassManager.App.Models;
using ClassManager.App.Services;
using ClassManager.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassManager.App.ViewModels;

/// <summary>
/// 关于窗口 ViewModel：只读展示作者与程序信息 + 卸载入口（作者信息不可编辑）+ 检查更新。
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IInstallService _install;
    private readonly IUpdateService _update;

    private Window? _owner;
    private UpdateManifest? _pendingUpdate;

    public AboutViewModel(ISettingsService settings, IInstallService install, IUpdateService update)
    {
        _settings = settings;
        _install = install;
        _update = update;

        var last = update.Settings.LastCheckedText ?? string.Empty;
        UpdateStatusText = last.Length > 0
            ? "上次检查：" + last
            : update.Settings.SourceUrl.Length > 0
                ? "尚未检查过更新"
                : "尚未配置更新源";
    }

    /// <summary>由窗口代码后置注入，用于给子窗口指定 Owner。</summary>
    public void AttachOwner(Window owner) => _owner = owner;

    public AuthorInfo Author => _settings.Current.Author;

    public string SoftwareName => "班级综合管理系统";

    public string VersionText => $"版本: {AppVersion.Display}";

    public string AuthorText => $"作者: {Author.Name}";

    public string ReleaseDateText => $"项目开启日期: {Author.ReleaseDate}";

    public string EmailText => $"邮箱: {Author.Email}";

    public string WebsiteText => $"网站: {Author.Website}";

    public string DescriptionText => Author.Description;

    public IReadOnlyList<string> Features { get; } = new[]
    {
        "✓ 学生成绩单导入和管理",
        "✓ 智能随机点名系统",
        "✓ 多种点名模式支持",
        "✓ 可视化座位管理",
        "✓ 多种座位排列算法",
        "✓ 座位表导出和打印",
        "✓ 讲评课件自动生成（Word → PPT）",
        "✓ AI 办公（可读写工作区文件）",
    };

    public string CopyrightText => "本软件仅供教学和管理使用，K. 版权所有";

    // ---------- 检查更新 ----------

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _updateVersionText = string.Empty;

    [ObservableProperty]
    private string _updateMetaText = string.Empty;

    [ObservableProperty]
    private string _updateNotes = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private int _downloadPercent;

    [RelayCommand]
    private async Task CheckUpdate()
    {
        IsCheckingUpdate = true;
        UpdateStatusText = "正在检查更新…";

        var result = await _update.CheckAsync();

        IsCheckingUpdate = false;
        HasUpdate = false;
        _pendingUpdate = null;

        if (!result.Ok)
        {
            UpdateStatusText = "✗ " + result.Message;
            return;
        }

        if (result.NotConfigured)
        {
            UpdateStatusText = "尚未配置更新源";
            OpenUpdateSource();
            return;
        }

        UpdateStatusText = (result.HasUpdate ? "● " : "✓ ") + result.Message;

        if (result.HasUpdate && result.Info is not null)
        {
            _pendingUpdate = result.Info;
            UpdateVersionText = result.Info.DisplayVersion;

            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Info.PublishedAt))
            {
                meta.Add(result.Info.PublishedAt);
            }

            if (result.Info.SizeBytes > 0)
            {
                meta.Add(result.Info.SizeText);
            }

            UpdateMetaText = string.Join(" · ", meta);
            UpdateNotes = string.IsNullOrWhiteSpace(result.Info.Notes) ? "（无更新说明）" : result.Info.Notes;
            HasUpdate = true;
        }
    }

    [RelayCommand]
    private async Task DownloadUpdate()
    {
        var info = _pendingUpdate;
        if (info is null || IsDownloading)
        {
            return;
        }

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "安装更新",
            Content = $"即将下载 {info.DisplayVersion}"
                      + (info.SizeBytes > 0 ? $"（{info.SizeText}）" : string.Empty)
                      + $" 并覆盖安装当前 {AppVersion.Display}。\n下载完成后本程序会退出，由新版本继续安装。",
            PrimaryButtonText = "立即更新",
            CloseButtonText = "稍后",
        };

        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        IsDownloading = true;
        DownloadPercent = 0;

        var progress = new Progress<int>(value => DownloadPercent = value);
        var result = await _update.DownloadAsync(info, progress);

        IsDownloading = false;

        if (!result.Ok)
        {
            await ShowMessageAsync("更新失败", result.Message);
            return;
        }

        if (!_update.Launch(result.FilePath))
        {
            await ShowMessageAsync("更新失败", "无法启动安装包：" + result.FilePath);
            return;
        }

        // 交给新包接管：它会自动覆盖安装旧版本
        Application.Current?.Shutdown();
    }

    /// <summary>
    /// 打开下载页：网页地址用浏览器打开；本地路径用资源管理器打开所在文件夹。
    /// 不需要自己搭服务器——更新源放在网盘/共享盘上就能用。
    /// </summary>
    [RelayCommand]
    private void OpenDownloadPage()
    {
        var info = _pendingUpdate;

        if (info is null)
        {
            return;
        }

        if (!_update.OpenDownloadPage(info))
        {
            ShowMessageAsync("打不开下载页", "请手动打开更新源所在文件夹下载新版本。").GetAwaiter().GetResult();
        }
    }

    [RelayCommand]
    private void IgnoreUpdate()
    {
        var info = _pendingUpdate;
        if (info is null)
        {
            return;
        }

        _update.Settings.IgnoredVersion = info.Version;
        _update.Save();

        _pendingUpdate = null;
        HasUpdate = false;
        UpdateStatusText = $"已忽略 {info.DisplayVersion}，之后不再提示。";
    }

    [RelayCommand]
    private void ConfigureUpdateSource() => OpenUpdateSource();

    private void OpenUpdateSource()
    {
        var window = new UpdateSourceWindow(_update)
        {
            Owner = _owner,
        };

        window.ShowDialog();

        var last = _update.Settings.LastCheckedText ?? string.Empty;
        if (last.Length > 0)
        {
            UpdateStatusText = "上次检查：" + last;
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "确认卸载",
            Content = "确定要卸载「班级综合管理系统」吗？",
            PrimaryButtonText = "是",
            CloseButtonText = "否",
        };

        var result = await confirm.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        // 优先启动独立卸载程序（从临时目录运行，不被待删目录锁住）；失败则回退内置清理。
        var launched = false;
        try
        {
            launched = _install.LaunchUninstaller(removeUserSettings: false);
        }
        catch
        {
            // 忽略：走回退路径
        }

        if (!launched)
        {
            try
            {
                _install.Uninstall(removeUserSettings: false);
            }
            catch
            {
                // 忽略
            }
        }

        await ShowMessageAsync(
            "卸载",
            launched
                ? "卸载程序已启动，本程序即将退出以完成清理。"
                : "已启动后台清理，本程序即将退出。");

        Application.Current?.Shutdown();
    }

    private static async Task ShowMessageAsync(string title, string content)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "确定",
            CloseButtonText = string.Empty,
        };

        await box.ShowDialogAsync();
    }
}
