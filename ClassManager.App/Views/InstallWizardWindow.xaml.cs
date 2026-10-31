using ClassManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>安装向导窗口。</summary>
public partial class InstallWizardWindow : FluentWindow
{
    public InstallWizardWindow(InstallWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
