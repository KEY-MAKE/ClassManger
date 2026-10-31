using System.Windows;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>
/// 导出 / 打印前的小选项窗口：
/// 是否包含列表头（第x列）与排标签（第x排）、是否包含班级名称。
/// 初始值 = 座位页面板开关值。
/// </summary>
public partial class SeatExportOptionsWindow : FluentWindow
{
    /// <summary>用户选择：是否包含列表头与排标签。</summary>
    public bool IncludeHeaders { get; private set; }

    /// <summary>用户选择：是否包含班级名称。</summary>
    public bool IncludeClassName { get; private set; }

    public SeatExportOptionsWindow(bool initialIncludeHeaders, bool initialIncludeClassName)
    {
        IncludeHeaders = initialIncludeHeaders;
        IncludeClassName = initialIncludeClassName;
        InitializeComponent();
        HeadersCheck.IsChecked = initialIncludeHeaders;
        ClassNameCheck.IsChecked = initialIncludeClassName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        IncludeHeaders = HeadersCheck.IsChecked == true;
        IncludeClassName = ClassNameCheck.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}