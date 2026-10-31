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
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
