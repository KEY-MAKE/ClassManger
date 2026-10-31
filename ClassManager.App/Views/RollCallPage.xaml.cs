using System.Windows.Controls;
using ClassManager.App.Services;

namespace ClassManager.App.Views;

/// <summary>点名系统页（DataContext 由 NavigationPageProvider 注入）。</summary>
public partial class RollCallPage : Page
{
    public RollCallPage()
    {
        InitializeComponent();

        // NavigationView 不给 Page 限高，这里把页面钉到宿主高度（详见 PageHeightBinder）
        Loaded += (_, _) => PageHeightBinder.Attach(this, Root);
    }
}
