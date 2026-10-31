using ClassManager.App.Models;
using Wpf.Ui.Controls;

namespace ClassManager.App.Services;

/// <summary>主题服务接口。</summary>
public interface IThemeService
{
    /// <summary>当前主题偏好。</summary>
    ThemePreference Preference { get; }

    /// <summary>应用主题（System → 读系统；Light/Dark → 覆盖）。</summary>
    void Apply(ThemePreference preference);

    /// <summary>绑定窗口 backdrop 与系统主题监听。</summary>
    void Attach(FluentWindow window);

    /// <summary>主题偏好变化事件。</summary>
    event EventHandler<ThemePreference>? Changed;

    /// <summary>
    /// 业务调色板变更事件（自绘控件需订阅以重绘）。
    /// 在显式主题应用与系统主题变化路径上都会触发。
    /// </summary>
    event EventHandler? PaletteChanged;
}
