using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassManager.App.Converters;

/// <summary>
/// 把宿主容器的 ActualHeight 转成页面可用的高度上限。
/// <para>
/// 为什么需要：WPF-UI 的 NavigationView 不会给承载的 Page 限制高度（实测页面被拉到 7744px），
/// 于是页面里的 <c>*</c> 行退化成「内容多高就多高」，ScrollViewer 没有滚动范围、
/// 底部输入栏被推到可视区之外。这里把页面高度显式钉到宿主高度上。
/// </para>
/// 取不到有效值（0 / NaN，例如宿主还没完成测量）时返回 PositiveInfinity 表示不限制，
/// 避免把页面压成 0 高而看不见。
/// </summary>
public sealed class HostHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d && !double.IsNaN(d) && d > 0)
        {
            return d;
        }

        return double.PositiveInfinity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
