using System.ComponentModel;
using System.Windows;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>主壳窗口：绑定 VM、初始化导航、关闭时保存窗口几何。</summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.SetSnackbarPresenter(SnackbarHost);

        RestoreGeometry();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 绑定系统主题监听与 Mica backdrop
        AppServices.Theme.Attach(this);

        // 页面提供器 + 初始导航
        RootNavigation.SetPageProviderService(AppServices.PageProvider);
        RootNavigation.Navigated += (_, _) => UpdateModuleFromSelection();
        RootNavigation.Navigate(typeof(RollCallPage));
        UpdateModuleFromSelection();
    }

    private void UpdateModuleFromSelection()
    {
        if (RootNavigation.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        // 直接取导航项上的文字（拿不到就退回页面类型名）。
        // 以前这里写死成「座位管理→座位管理系统，其它一律点名系统」，
        // 结果讲评课件 / AI 办公 都被错误显示成「点名系统」。
        // 现在新增模块只要加到导航栏就会自动对应，不需要再改这里。
        var module = item.Content as string;

        if (string.IsNullOrWhiteSpace(module) && item.TargetPageType is not null)
        {
            module = item.TargetPageType.Name.EndsWith("Page", StringComparison.Ordinal)
                ? item.TargetPageType.Name[..^4]
                : item.TargetPageType.Name;
        }

        if (!string.IsNullOrWhiteSpace(module))
        {
            _viewModel.SetCurrentModule(module);
        }
    }

    private void RestoreGeometry()
    {
        var settings = AppServices.Settings.Current;

        if (settings.WindowWidth >= MinWidth)
        {
            Width = settings.WindowWidth;
        }

        if (settings.WindowHeight >= MinHeight)
        {
            Height = settings.WindowHeight;
        }

        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        var settings = AppServices.Settings.Current;
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;

        if (WindowState == WindowState.Normal)
        {
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
        }

        AppServices.Settings.Save();
    }
}
