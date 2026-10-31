using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>
/// 学生名单侧栏（独立窗口）：每行 3 个，把名字「拖」到座位上完成安排。
/// 进入座位管理模块时出现，离开时关闭。
/// </summary>
public partial class StudentPaletteWindow : FluentWindow
{
    private readonly List<StudentChip> _all;
    private Point _dragStart;
    private bool _dragArmed;

    public StudentPaletteWindow(IEnumerable<StudentChip> chips)
    {
        InitializeComponent();

        _all = chips.ToList();
        ChipList.ItemsSource = _all;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;

        ChipList.ItemsSource = keyword.Length == 0
            ? _all
            : _all.Where(c => c.Name.Contains(keyword, StringComparison.Ordinal)).ToList();
    }

    private void OnChipMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = true;
    }

    private void OnChipMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;

        if (sender is FrameworkElement { DataContext: StudentChip chip })
        {
            // 用纯文本承载姓名：跨窗口拖放最稳，不依赖自定义数据格式
            DragDrop.DoDragDrop(this, chip.Name, DragDropEffects.Copy);
        }
    }
}
