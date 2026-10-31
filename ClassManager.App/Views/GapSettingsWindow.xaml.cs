using System.Collections.ObjectModel;
using System.Windows;
using ClassManager.App.Models;
using ClassManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>
/// 间距设置窗口：统一填充 + 逐道列 / 行间隙编辑 + 分组阈值。
/// 确定后把结果写回 <see cref="SeatLayout"/>（会话内有效，不持久化）。
/// </summary>
public partial class GapSettingsWindow : FluentWindow
{
    private readonly SeatLayout _layout;

    /// <summary>列间隙行集合（长度 = Cols - 1）。</summary>
    public ObservableCollection<GapSettingItem> ColumnItems { get; } = new();

    /// <summary>行间隙行集合（长度 = Rows - 1）。</summary>
    public ObservableCollection<GapSettingItem> RowItems { get; } = new();

    public GapSettingsWindow(SeatLayout layout)
    {
        _layout = layout;
        InitializeComponent();

        for (var i = 0; i < layout.ColumnGaps.Count; i++)
        {
            ColumnItems.Add(new GapSettingItem($"第{i + 1}列 ↔ 第{i + 2}列", layout.ColumnGaps[i]));
        }

        for (var i = 0; i < layout.RowGaps.Count; i++)
        {
            RowItems.Add(new GapSettingItem($"第{i + 1}排 ↔ 第{i + 2}排", layout.RowGaps[i]));
        }

        ColumnList.ItemsSource = ColumnItems;
        RowList.ItemsSource = RowItems;
        ColumnListHeader.Text = $"列间距（{ColumnItems.Count} 项）";
        RowListHeader.Text = $"行间距（{RowItems.Count} 项）";
    }

    private void OnApplyAllColumns(object sender, RoutedEventArgs e)
    {
        var value = SeatLayout.NormalizeGap(ColumnAllBox.Value ?? SeatLayout.DefaultGap);
        foreach (var item in ColumnItems)
        {
            item.Value = value;
        }
    }

    private void OnApplyAllRows(object sender, RoutedEventArgs e)
    {
        var value = SeatLayout.NormalizeGap(RowAllBox.Value ?? SeatLayout.DefaultGap);
        foreach (var item in RowItems)
        {
            item.Value = value;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        for (var i = 0; i < ColumnItems.Count; i++)
        {
            _layout.SetColumnGap(i, ColumnItems[i].Value);
        }

        for (var i = 0; i < RowItems.Count; i++)
        {
            _layout.SetRowGap(i, RowItems[i].Value);
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
