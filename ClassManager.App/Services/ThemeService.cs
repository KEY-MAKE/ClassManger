using System.Windows;
using System.Windows.Media;
using ClassManager.App.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClassManager.App.Services;

/// <summary>
/// 主题实现：跟随系统 / 手动覆盖 + 持久化由调用方负责 + 窗口 backdrop。
/// 同时根据明暗主题刷新业务调色板（Palette.xaml）。
/// </summary>
public sealed class ThemeService : IThemeService
{
    private static readonly (string Key, string Light, string Dark)[] PaletteMap =
    {
        // 座位画布已改为固定蓝白（SeatMapPalette），不再依赖主题刷；此处仅保留仍被引用的业务键。
        ("SeatSelectedBrush", "#0078D4", "#4CC2FF"),
        ("SeatedChipBrush", "#C8E6C9", "#2E4A34"),
    };

    private bool _systemHooked;

    /// <summary>
    /// 业务调色板变更事件：自绘控件（如 SeatMapControl 的 Canvas 图形）需订阅此事件以重绘。
    /// 在 <see cref="Apply"/> 与系统主题变化路径上都会触发。
    /// </summary>
    public event EventHandler? PaletteChanged;

    /// <inheritdoc />
    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    /// <inheritdoc />
    public event EventHandler<ThemePreference>? Changed;

    /// <inheritdoc />
    public void Apply(ThemePreference preference)
    {
        Preference = preference;
        var appTheme = ResolveApplicationTheme(preference);
        ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, true);
        ApplyPalette(appTheme == ApplicationTheme.Dark);

        // 通知自绘控件重绘（Application.Resources 内的刷被冻结，无法原地改 Color，
        // 只能替换实例；替换实例后 DynamicResource 会自动刷新，但自绘图形需显式重绘）。
        PaletteChanged?.Invoke(this, EventArgs.Empty);

        Changed?.Invoke(this, preference);
    }

    /// <inheritdoc />
    public void Attach(FluentWindow window)
    {
        SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);

        if (!_systemHooked)
        {
            ApplicationThemeManager.Changed += OnSystemThemeChanged;
            _systemHooked = true;
        }
    }

    private void OnSystemThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (Preference != ThemePreference.System)
        {
            return;
        }

        ApplyPalette(theme == ApplicationTheme.Dark);
        PaletteChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ApplicationTheme ResolveApplicationTheme(ThemePreference preference)
    {
        switch (preference)
        {
            case ThemePreference.Light:
                return ApplicationTheme.Light;
            case ThemePreference.Dark:
                return ApplicationTheme.Dark;
            default:
                var systemTheme = SystemThemeManager.GetCachedSystemTheme();
                return systemTheme == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
    }

    private static void ApplyPalette(bool isDark)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        foreach (var (key, light, dark) in PaletteMap)
        {
            var color = (Color)ColorConverter.ConvertFromString(isDark ? dark : light);

            // Application.Resources 是密封的资源字典，任何放入其中的 Freezable 都会被冻结
            // （实测：C# new 出来的 SolidColorBrush 一旦装入 Application.Resources，IsFrozen 即为 True，
            //  再改 Color 会抛「处于只读状态」）。因此只能**替换实例**而非原地改色。
            // 替换实例后，DynamicResource 消费者会自动刷新；自绘控件通过 PaletteChanged 事件重绘。
            app.Resources[key] = new SolidColorBrush(color);
        }
    }
}
