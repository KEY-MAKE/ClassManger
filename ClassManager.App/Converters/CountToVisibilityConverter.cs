using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassManager.App.Converters;

/// <summary>
/// 集合计数 → Visibility：计数为 0 时显示（用于空状态占位）。
/// ConverterParameter = "Invert" 时计数非 0 才显示。
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            null => 0,
            _ => 0,
        };

        var isEmpty = count == 0;
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        if (invert)
        {
            isEmpty = !isEmpty;
        }

        return isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
