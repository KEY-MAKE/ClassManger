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
    private readonly UpdateDownloadManager _download;

    private Window? _owner;
    private UpdateManifest? _pendingUpdate;

    public AboutViewModel(ISettingsService settings, IInstallService install, IUpdateService update)
    {
        _settings = settings;
        _install = install;
        _update = update;
        _download = AppServices.UpdateDownload;

        // 下载跑在应用级单例里，窗口关掉也不会停；这里接上它当前的状态，
        // 所以重新打开「关于」能直接看到进度。
        _download.Changed += OnDownloadChanged;
        SyncDownloadState();

        // 更新源已内置（用户不可更改），所以不存在「未配置」这种状态
        var last = update.Settings.LastCheckedText ?? string.Empty;
        UpdateStatusText = last.Length > 0 ? "上次检查：" + last : "尚未检查过更新";
    }

    /// <summary>由窗口代码后置注入，用于给子窗口指定 Owner。</summary>
    public void AttachOwner(Window owner) => _owner = owner;

    /// <summary>窗口关闭时调用：退订，避免已销毁的 ViewModel 继续被回调。</summary>
    public void Detach() => _download.Changed -= OnDownloadChanged;

    /// <summary>后台下载状态变化 → 切回 UI 线程刷新。</summary>
    private void OnDownloadChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(SyncDownloadState));
    }

    /// <summary>把后台下载状态映射到界面属性。</summary>
    private void SyncDownloadState()
    {
        IsDownloading = _download.IsDownloading;
        DownloadPercent = _download.Percent;

        if (_download.State is UpdateDownloadManager.DownloadState.Failed)
        {
            UpdateStatusText = "✗ " + _download.Message;
        }
    }

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

        // 已经在后台下载同一个版本 → 不重复确认、不重复启动
        if (info is null || _download.IsDownloading)
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

        // 下载交给应用级管理器：窗口关掉也继续下，完成后再启动新包安装。
        // 不在这里 await —— 这里 await 会让进度回调绑死在当前窗口的 UI 上下文中，
        // 窗口一关就抛异常把下载打断（就是之前的「关窗口 → 下载失败」）。
        try
        {
            _download.Start(info);
        }
        catch (Exception ex)
        {
            // 启动下载失败也不能把程序带崩 —— 老师们的电脑上出这种情况，
            // 用户看到的是「闪退」，连错误提示都没有。
            IsDownloading = false;
            UpdateStatusText = "✗ 无法开始下载：" + ex.Message;
            await ShowMessageAsync("更新失败", $"无法开始下载：{ex.Message}");
            return;
        }

        IsDownloading = true;
        DownloadPercent = 0;
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
