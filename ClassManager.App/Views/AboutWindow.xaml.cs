using System.Windows;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>关于窗口。</summary>
public partial class AboutWindow : FluentWindow
{
    public AboutWindow(ISettingsService settings, IInstallService install, IUpdateService update)
    {
        InitializeComponent();

        var viewModel = new AboutViewModel(settings, install, update);
        DataContext = viewModel;
        viewModel.AttachOwner(this);

        // 窗口关掉就退订 —— 下载在应用级管理器里继续跑，
        // 但这个 ViewModel 已经没用了，不该再被回调。
        Closed += (_, _) => viewModel.Detach();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
