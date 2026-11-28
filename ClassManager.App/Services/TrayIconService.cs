using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClassManager.App.Views;
using Hardcodet.Wpf.TaskbarNotification;

namespace ClassManager.App.Services;

/// <summary>
/// 系统托盘图标。
/// <para>
/// <b>为什么要有</b>：老师上课时把主窗口关了，但程序还得活着 —— 悬浮球要靠它点名。
/// 所以「关闭主窗口」只是收起来，真正的退出走托盘右键菜单。
/// </para>
/// <para>
/// 用 Hardcodet.NotifyIcon.Wpf：纯 WPF 实现，不引入 WinForms
/// （开 UseWindowsForms 会让 Application / UserControl / Color / Point 全部产生歧义，
/// 整个项目都要加完全限定名，代价太大）。
/// </para>
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _taskbar;
    private bool _disposed;
    private bool _balloonShown;

    public TrayIconService(MainWindow mainWindow)
    {
        _taskbar = new TaskbarIcon
        {
            ToolTipText = "班级综合管理系统（在后台运行）",
            Visibility = Visibility.Visible,
        };

        try
        {
            // Class.png 是 256×256 的圆徽章，系统会自己缩放到托盘尺寸
            _taskbar.IconSource = new BitmapImage(
                new Uri("pack://application:,,,/Assets/Class.png"));
        }
        catch
        {
            // 图标读不出来也不影响功能
        }

        _taskbar.TrayMouseDoubleClick += (_, _) => ShowMainWindow(mainWindow);
        _taskbar.ContextMenu = BuildMenu(mainWindow);
    }

    private ContextMenu BuildMenu(MainWindow mainWindow)
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "打开主界面", FontWeight = FontWeights.Bold };
        open.Click += (_, _) => ShowMainWindow(mainWindow);
        menu.Items.Add(open);

        var ball = new MenuItem { Header = "显示悬浮球" };
        ball.Click += (_, _) => ShowFloatingBall();
        menu.Items.Add(ball);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        return menu;
    }

    /// <summary>第一次收进托盘时提示一下，免得用户以为程序崩了。</summary>
    public void ShowHideHintOnce()
    {
        if (_balloonShown)
        {
            return;
        }

        _balloonShown = true;

        try
        {
            _taskbar.ShowBalloonTip(
                "程序还在后台运行",
                "点这个图标能叫回窗口；要彻底退出请右键选「退出」。",
                BalloonIcon.Info);
        }
        catch
        {
            // 系统禁用了气泡提示就算了
        }
    }

    private static void ShowMainWindow(MainWindow mainWindow)
    {
        try
        {
            if (!mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            if (mainWindow.WindowState == WindowState.Minimized)
            {
                mainWindow.WindowState = WindowState.Normal;
            }

            mainWindow.Activate();

            // 从托盘叫回来的窗口经常躲在别的窗口后面，闪一下置顶把它提到最前
            mainWindow.Topmost = true;
            mainWindow.Topmost = false;
            mainWindow.Focus();
        }
        catch
        {
            // 窗口可能已经销毁
        }
    }

    private static void ShowFloatingBall()
    {
        try
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (window is FloatingBallWindow ball)
                {
                    ball.Show();
                    ball.Activate();
                    return;
                }
            }

            new FloatingBallWindow().Show();
        }
        catch
        {
            // 忽略
        }
    }

    private static void ExitApplication()
    {
        try
        {
            AppServices.Settings.Save();
        }
        catch
        {
            // 忽略
        }

        // 主窗口的 OnClosing 会拦下关闭请求，这里先放行
        if (Application.Current.MainWindow is MainWindow main)
        {
            main.AllowRealClose = true;
        }

        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _taskbar.Visibility = Visibility.Collapsed;
            _taskbar.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}
