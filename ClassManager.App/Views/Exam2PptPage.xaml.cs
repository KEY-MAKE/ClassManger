using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;

namespace ClassManager.App.Views;

/// <summary>讲评课件页：承载参数区与引擎日志，并支持把 Word/PPT 文件拖进页面自动分配角色。</summary>
public partial class Exam2PptPage : Page
{
    private Exam2PptViewModel? _viewModel;

    public Exam2PptPage()
    {
        InitializeComponent();

        // NavigationView 不给 Page 限高，这里把页面钉到宿主高度（详见 PageHeightBinder）
        Loaded += (_, _) => PageHeightBinder.Attach(this, Root);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as Exam2PptViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Exam2PptViewModel.LogText))
        {
            LogBox.ScrollToEnd();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel?.ApplyDroppedFiles(paths);
        }

        e.Handled = true;
    }
}
