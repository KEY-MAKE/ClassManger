using System;
using System.Globalization;
using System.Windows.Data;

namespace ClassManager.App.Converters;

/// <summary>
/// 布尔取反。
/// <para>
/// 主要给「一对互斥的单选按钮绑同一个 bool」用：
/// 文科那个绑 <c>Converter={StaticResource InverseBooleanConverter}</c>，
/// 理科那个直接绑原值 —— 这样两个 RadioButton 共享一个属性，不用写 setter 互斥逻辑。
/// </para>
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}
