using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassManager.App.Services;

/// <summary>
/// 把页面高度钉到宿主（NavigationView）的实测高度上。
/// <para>
/// 根因：WPF-UI 的 NavigationView 不会给承载的 Page 限制高度。实测 AI 办公页被拉到 7744px，
/// 结果页面里的 <c>*</c> 行退化成「内容多高就多高」——消息区 ScrollViewer 没有滚动范围
/// （滚轮失效、只能拖滚动条），底部输入栏被推到可视区之外。
/// </para>
/// 实测修复后：页面根 7744.5 → 721，消息区可滚范围 0 → 7023.5，输入框回到可视区内。
/// </summary>
public static class PageHeightBinder
{
    /// <summary>给页面绑定宿主高度（在 Loaded 里调用一次即可）。</summary>
    public static void Attach(Page page, FrameworkElement root)
    {
        if (page is null || root is null)
        {
            return;
        }

        void Apply(double height)
        {
            if (height > 0 && !double.IsNaN(height) && !double.IsInfinity(height))
            {
                root.MaxHeight = height;
            }
        }

        var host = FindAncestor(page, d => d is Wpf.Ui.Controls.NavigationView) as FrameworkElement;

        if (host is not null)
        {
            Apply(host.ActualHeight);
            host.SizeChanged += (_, _) => Apply(host.ActualHeight);
            return;
        }

        // 兜底：找不到 NavigationView 时用主窗口高度减去标题栏与状态栏
        var window = Window.GetWindow(page);
        if (window is not null)
        {
            Apply(window.ActualHeight - 120);
            window.SizeChanged += (_, _) => Apply(window.ActualHeight - 120);
        }
    }

    private static DependencyObject? FindAncestor(DependencyObject start, Func<DependencyObject, bool> match)
    {
        var current = start;

        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is not null && match(current))
            {
                return current;
            }
        }

        return null;
    }
}
