using System.Windows;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>点名统计窗口。</summary>
public partial class RollCallStatsWindow : FluentWindow
{
    public RollCallStatsWindow(IRollCallService rollCall)
    {
        InitializeComponent();
        DataContext = new RollCallStatsViewModel(rollCall);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
