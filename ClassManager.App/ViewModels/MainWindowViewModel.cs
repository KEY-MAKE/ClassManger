using System.IO;
using System.Windows;
using ClassManager.App.Models;
using ClassManager.App.Services;
using ClassManager.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using IThemeService = ClassManager.App.Services.IThemeService;

namespace ClassManager.App.ViewModels;

/// <summary>
/// 主壳 ViewModel：标题 / 班级信息 / 状态栏 / 统一 Toast / 主题切换 / 关于 / 卸载 / 共享导入。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IShell
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IImportService _import;
    private readonly IInstallService _install;
    private readonly IUpdateService _update;

    /// <summary>统一 Toast 服务。</summary>
    public Wpf.Ui.SnackbarService Snackbar { get; } = new();

    /// <summary>启动时静默检查到新版本时置 true（关于按钮会带红点提示）。</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    public string AboutButtonText => HasUpdate ? "关于 ● 有更新" : "关于";

    partial void OnHasUpdateChanged(bool value) => OnPropertyChanged(nameof(AboutButtonText));

    [ObservableProperty]
    private string _className = "未导入班级数据";

    [ObservableProperty]
    private string _statusMessage = "就绪 - 请导入班级成绩单";

    [ObservableProperty]
    private string _currentModuleName = "点名系统";

    [ObservableProperty]
    private ThemePreference _themePreference = ThemePreference.Light;

    public MainWindowViewModel()
    {
        _settings = AppServices.Settings;
        _theme = AppServices.Theme;
        _import = AppServices.Import;
        _install = AppServices.Install;
        _update = AppServices.Update;

        _themePreference = _settings.Current.Theme;
    }

    /// <summary>窗口标题（软件名 + 作者）。</summary>
    public string TitleText => $"班级综合管理系统    作者：{_settings.Current.Author.Name}";

    /// <summary>主题切换按钮提示（仅两态：浅色 / 深色）。</summary>
    public string ThemeButtonText =>
        ThemePreference == ThemePreference.Dark ? "主题：深色" : "主题：浅色";

    /// <summary>设置 Snackbar 宿主（由 MainWindow 代码后置注入）。</summary>
    public void SetSnackbarPresenter(SnackbarPresenter presenter) =>
        Snackbar.SetSnackbarPresenter(presenter);

    /// <inheritdoc />
    public void SetStatus(string message) => StatusMessage = message;

    /// <inheritdoc />
    public void SetCurrentModule(string moduleName)
    {
        CurrentModuleName = moduleName;
        SetStatus($"当前模块: {moduleName}");
    }

    /// <inheritdoc />
    public void ShowToast(string title, string message, ControlAppearance appearance)
    {
        try
        {
            Snackbar.Show(title, message, appearance, new SymbolIcon(SymbolRegular.Info24), TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Snackbar 未就绪时忽略，状态栏仍会反馈
        }
    }

    /// <summary>启动时静默检查一次更新；只提示，不自动下载。</summary>
    public async Task CheckUpdateOnStartupAsync()
    {
        try
        {
            if (!_update.Settings.AutoCheckOnStartup)
            {
                return;
            }

            // 让主界面先渲染出来，避免把网络等待压在启动路径上
            await Task.Delay(TimeSpan.FromSeconds(2));

            var result = await _update.CheckAsync();
            if (result.Ok && result.HasUpdate && result.Info is not null)
            {
                NotifyUpdateAvailable(result.Info.Version);
            }
        }
        catch
        {
            // 启动检查失败一律静默：断网/更新源不可达不该影响用软件
        }
    }

    /// <summary>标记「有更新」并在状态栏与 Toast 提示一次。</summary>
    public void NotifyUpdateAvailable(string version)
    {
        HasUpdate = true;
        SetStatus($"发现新版本 v{version}：打开「关于 → 检查更新」即可下载安装");
        ShowToast("发现新版本", $"v{version} 已发布，可在「关于」中下载安装", ControlAppearance.Info);
    }

    /// <summary>执行一次导入（供 ImportCommand 与各页面共用）。</summary>
    public async Task ImportRosterAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择成绩单文件",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx|CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
        };

        var lastDir = _settings.Current.LastImportDirectory;
        if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
        {
            dialog.InitialDirectory = lastDir;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportRosterFromPathAsync(dialog.FileName);
    }

    /// <summary>
    /// 按给定文件路径导入成绩单（供命令行 `--import` 与文件关联直接打开使用）。
    /// </summary>
    public async Task ImportRosterFromPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetStatus($"导入失败: 文件不存在 {path}");
            ShowToast("导入失败", $"文件不存在: {path}", ControlAppearance.Danger);
            return;
        }

        if (_import.IsLegacyXls(path))
        {
            await ShowMessageAsync("错误", "暂不支持旧版 .xls（二进制）文件，请用 Excel 另存为 .xlsx 或 .csv 后重试。");
            return;
        }

        SetStatus("正在导入...");

        try
        {
            var result = _import.Import(path, _settings.Current.GenerateDemoScores);

            // 先解析成功再替换状态，避免破坏已有数据
            AppServices.ApplyRoster(result);

            ClassName = $"班级: {result.ClassName} | 人数: {result.Students.Count}";
            SetStatus($"已导入文件: {Path.GetFileName(result.FilePath)}，共 {result.Students.Count} 名学生");

            _settings.Current.LastImportDirectory = Path.GetDirectoryName(path);
            _settings.Save();

            ShowToast("导入成功", $"成功导入 {result.Students.Count} 名学生，已按成绩排序", ControlAppearance.Success);

            var notice = new List<string>();

            // 筛选隐藏行：说清楚为什么人数比表里少，否则老师会以为丢人了
            if (result.SkippedHiddenRows > 0)
            {
                notice.Add($"表格里有 {result.SkippedHiddenRows} 行被筛选隐藏，已自动跳过，只导入可见的 {result.Students.Count} 人。");
            }

            if (!string.IsNullOrWhiteSpace(result.FilterNotice))
            {
                notice.Add(result.FilterNotice!);
            }

            if (!string.IsNullOrWhiteSpace(result.NameColumnNotice))
            {
                notice.Add(result.NameColumnNotice!);
            }

            if (result.GeneratedDemoScores)
            {
                notice.Add("未找到成绩列，已生成随机成绩（仅供演示）。");
            }

            if (notice.Count > 0)
            {
                await ShowMessageAsync("提示", string.Join(Environment.NewLine, notice));
            }
        }
        catch (ImportException ex)
        {
            await ShowMessageAsync("导入失败", ex.Message);
            SetStatus($"导入失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("导入失败", $"读取文件时出错: {ex.Message}");
            SetStatus($"导入失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 启动时恢复上次导入的名单，省掉每次开机重新选文件。
    /// <para>
    /// 必须在主窗口 Show() 之后调用 —— 点名页 / 座位页要先把
    /// <see cref="AppServices.RosterImported"/> 订阅上，否则数据推不过去。
    /// </para>
    /// </summary>
    public Task RestoreLastRosterAsync()
    {
        var restored = RosterStore.TryLoad();

        if (restored is null)
        {
            return Task.CompletedTask;
        }

        AppServices.ApplyRoster(restored);

        ClassName = $"班级: {restored.ClassName} | 人数: {restored.Students.Count}";
        SetStatus($"已恢复上次名单: {restored.ClassName}，共 {restored.Students.Count} 名学生");

        ShowToast(
            "已恢复上次名单",
            $"{restored.ClassName} · {restored.Students.Count} 人（可直接点名）",
            ControlAppearance.Success);

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task Import() => ImportRosterAsync();

    [RelayCommand]
    private void ToggleTheme()
    {
        // 两态切换：浅色 ↔ 深色（不再有「跟随系统」）
        ThemePreference = ThemePreference == ThemePreference.Light
            ? ThemePreference.Dark
            : ThemePreference.Light;
    }

    partial void OnThemePreferenceChanged(ThemePreference value)
    {
        _theme.Apply(value);
        _settings.Current.Theme = value;
        _settings.Save();
        OnPropertyChanged(nameof(ThemeButtonText));
        ShowToast("主题", $"已切换为 {ThemeButtonText}", ControlAppearance.Info);
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var window = new AboutWindow(_settings, _install, _update)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "确认卸载",
            Content = "确定要卸载「班级综合管理系统」吗？\n将删除安装目录、快捷方式与卸载注册表项。",
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
            catch (Exception ex)
            {
                await ShowMessageAsync("卸载失败", ex.Message);
                return;
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
