using System.Windows;

namespace ClassManager.App.Services;

/// <summary>窗口激活服务接口。</summary>
public interface IWindowActivationService
{
    /// <summary>把窗口恢复并置于前台。</summary>
    void BringToFront(Window window);
}
