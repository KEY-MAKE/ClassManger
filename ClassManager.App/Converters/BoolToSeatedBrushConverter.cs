using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClassManager.App.Converters;

/// <summary>
/// 学生是否已排座 → 按钮底色：已排座使用 SeatedChipBrush（绿），否则使用默认控件填充色。
/// </summary>
public sealed class BoolToSeatedBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var seated = value is bool b && b;
        var key = seated ? "SeatedChipBrush" : "ControlFillColorDefaultBrush";
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
