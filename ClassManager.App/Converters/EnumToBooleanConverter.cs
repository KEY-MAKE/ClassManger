using System.Globalization;
using System.Windows.Data;

namespace ClassManager.App.Converters;

/// <summary>
/// 枚举 ↔ bool：用于 RadioButton 绑定枚举属性。
/// ConverterParameter 为枚举成员名。
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool flag && flag && parameter is not null)
        {
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (enumType.IsEnum)
            {
                return Enum.Parse(enumType, parameter.ToString()!, true);
            }
        }

        return Binding.DoNothing;
    }
}
