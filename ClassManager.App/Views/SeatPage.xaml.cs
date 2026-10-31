using System.Windows;
using System.Windows.Controls;
using ClassManager.App.Controls;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;

namespace ClassManager.App.Views;

/// <summary>
/// 座位管理页：桥接画布的点击 / 拖放，并在进入本模块时弹出「学生名单」侧栏窗口。
/// </summary>
public partial class SeatPage : Page
{
    private StudentPaletteWindow? _palette;

    public SeatPage()
    {
        InitializeComponent();

        // NavigationView 不给 Page 限高，这里把页面钉到宿主高度（详见 PageHeightBinder）
        Loaded += (_, _) => PageHeightBinder.Attach(this, Root);
        DataContextChanged += OnDataContextChanged;

        // 进入模块出现、离开模块消失
        Loaded += (_, _) => ShowPalette();
        Unloaded += (_, _) => ClosePalette();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (SeatMap is not null)
        {
            SeatMap.SeatClicked -= OnSeatClicked;
            SeatMap.SeatClicked += OnSeatClicked;

            SeatMap.SeatDropped -= OnSeatDropped;
            SeatMap.SeatDropped += OnSeatDropped;
        }
    }

    private void OnSeatClicked(object? sender, SeatClickEventArgs e)
    {
        if (DataContext is SeatViewModel viewModel)
        {
            viewModel.OnSeatClicked(e.Row, e.Col);
        }
    }

    private void OnSeatDropped(object? sender, SeatDropEventArgs e)
    {
        if (DataContext is SeatViewModel viewModel)
        {
            viewModel.AssignStudentToSeat(e.StudentName, e.Row, e.Col);
        }
    }

    private void OnShowPaletteClick(object sender, RoutedEventArgs e) => ShowPalette();

    /// <summary>显示（或聚焦）学生名单侧栏。</summary>
    public void ShowPalette()
    {
        if (DataContext is not SeatViewModel viewModel)
        {
            return;
        }

        if (_palette is { IsVisible: true })
        {
            _palette.Activate();
            return;
        }

        var owner = Window.GetWindow(this);

        _palette = new StudentPaletteWindow(viewModel.StudentChips)
        {
            Owner = owner,
        };

        if (owner is not null)
        {
            _palette.Left = Math.Max(0, owner.Left + owner.ActualWidth - _palette.Width - 28);
            _palette.Top = owner.Top + 110;
        }

        _palette.Closed += (_, _) => _palette = null;
        _palette.Show();
    }

    private void ClosePalette()
    {
        var palette = _palette;
        _palette = null;

        try
        {
            palette?.Close();
        }
        catch
        {
            // 忽略
        }
    }
}
