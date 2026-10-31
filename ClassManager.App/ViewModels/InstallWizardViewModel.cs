using System.IO;
using System.Windows;
using ClassManager.App.Models;
using ClassManager.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ClassManager.App.ViewModels;

/// <summary>
/// 安装向导 ViewModel：目录选择 / 复选框 / 进度 / 安装后可启动。
/// </summary>
public sealed partial class InstallWizardViewModel : ObservableObject
{
    private readonly IInstallService _install;

    [ObservableProperty]
    private string _installDirectory;

    [ObservableProperty]
    private bool _createDesktopShortcut = true;

    [ObservableProperty]
    private bool _createStartMenuShortcut = true;

    [ObservableProperty]
    private bool _launchAfterInstall = true;

    [ObservableProperty]
    private string _progressText = "准备就绪。";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isCompleted;

    public InstallWizardViewModel()
    {
        _install = AppServices.Install;
        _installDirectory = _install.DefaultInstallDirectory;
    }

    /// <summary>安装包大小提示（供界面展示）。</summary>
    public string SourceDirectoryHint => $"来源目录：{_install.CurrentProcessDirectory}";

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择安装目录",
            InitialDirectory = Directory.Exists(InstallDirectory)
                ? InstallDirectory
                : _install.DefaultInstallDirectory,
        };

        if (dialog.ShowDialog() == true)
        {
            InstallDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task Install()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InstallDirectory))
        {
            ProgressText = "请选择安装目录。";
            return;
        }

        IsBusy = true;
        ProgressText = "正在安装...";

        var options = new InstallOptions
        {
            InstallDirectory = InstallDirectory,
            CreateDesktopShortcut = CreateDesktopShortcut,
            CreateStartMenuShortcut = CreateStartMenuShortcut,
            LaunchAfterInstall = LaunchAfterInstall,
        };

        var progress = new Progress<string>(message => ProgressText = message);

        try
        {
            await Task.Run(() => _install.Install(options, progress));
            IsCompleted = true;
            ProgressText = "安装完成！";
        }
        catch (Exception ex)
        {
            ProgressText = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Finish()
    {
        try
        {
            if (LaunchAfterInstall && IsCompleted)
            {
                _install.LaunchInstalledCopy(InstallDirectory);
            }
        }
        catch
        {
            // 启动失败不阻塞退出
        }

        Application.Current?.Shutdown();
    }
}
