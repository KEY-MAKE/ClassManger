using Wpf.Ui.Controls;

namespace ClassManager.App.Services;

/// <summary>
/// 壳（主窗口）对外暴露的能力：供各页面 ViewModel 回传状态、触发共享导入、弹出提示。
/// </summary>
public interface IShell
{
    /// <summary>设置底部状态栏文本。</summary>
    void SetStatus(string message);

    /// <summary>设置当前模块名（切换模块时调用）。</summary>
    void SetCurrentModule(string moduleName);

    /// <summary>弹出统一 Toast。</summary>
    void ShowToast(string title, string message, ControlAppearance appearance);

    /// <summary>触发共享的成绩单导入流程（打开对话框 → 解析 → 广播）。</summary>
    Task ImportRosterAsync();
}
