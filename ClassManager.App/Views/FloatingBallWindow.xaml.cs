using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassManager.App.Models;
using ClassManager.App.Services;

namespace ClassManager.App.Views;

/// <summary>
/// 桌面悬浮球：收起是一个球，点开是罗盘，专为「放幻灯片时也能点名」设计。
/// <para>
/// <b>置顶</b>：窗口 Topmost + 定时 SetWindowPos(HWND_TOPMOST) 兜底 —— 光靠 Topmost
/// 有时会被别的置顶窗口压下去（尤其是 PowerPoint 全屏放映结束后重新抢焦点）。
/// </para>
/// <para>
/// <b>不挡操作</b>：点完名自动收起回小球，名字在球上显示几秒，不弹窗、不切走放映。
/// </para>
/// </summary>
public partial class FloatingBallWindow : Window
{
    private const double CollapsedSize = 72;
    private const double ExpandedSize = 340;

    private bool _expanded;
    private bool _moved;
    private double _dragStartLeft;
    private double _dragStartTop;
    private DispatcherTimer? _nameTimer;
    private RollCallResultPopup? _resultPopup;
    private DispatcherTimer? _topmostGuard;

    public FloatingBallWindow()
    {
        InitializeComponent();

        // 悬浮球是"贴纸"，不该抢焦点：
        // 不设这两个的话，主窗口刚打开就失去焦点，用户得点一下任务栏才能操作。
        ShowActivated = false;

        Width = CollapsedSize;
        Height = CollapsedSize;

        // 放在屏幕右下角，留一点边距
        var area = SystemParameters.WorkArea;
        Left = area.Right - CollapsedSize - 36;
        Top = area.Bottom - CollapsedSize - 90;

        // DragMove 期间靠位置变化判断「到底有没有拖动」
        LocationChanged += (_, _) =>
        {
            if (Math.Abs(Left - _dragStartLeft) > 2 || Math.Abs(Top - _dragStartTop) > 2)
            {
                _moved = true;
            }
        };

        Loaded += (_, _) =>
        {
            KeepOnTop();
            StartTopmostGuard();
        };

        // 不再挂 WM_NCHITTEST 做穿透：
        // 那套「圆外返回 HTTRANSPARENT」的做法在实测里导致整个窗口连左键都收不到
        // （右键菜单是系统级消息还能弹，所以表现为「右键能用、左键死了」）。
        // 先保证球能点能拖，挡到东西的问题用「拖走 / 右键隐藏」解决。

        Closed += (_, _) =>
        {
            _nameTimer?.Stop();
            _topmostGuard?.Stop();
        };
    }

    // ---------------- 拖动 / 点击 ----------------

    private static void Log(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm_ball.log");
            System.IO.File.AppendAllText(
                path,
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n",
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    /// <summary>
    /// 按下 → 交给系统的窗口拖动循环（DragMove），松手后判断是「拖动」还是「单击」。
    /// <para>
    /// <b>为什么不用 CaptureMouse</b>：上一版手动 CaptureMouse + 等 MouseUp 释放，
    /// 实测 MouseUp 没到，捕获就一直挂着 —— 鼠标被这个 72×72 的小窗独占，
    /// 整个主界面都点不动，而右键（走系统消息）却能正常弹菜单。
    /// DragMove 是系统级的移动循环，松手必释放，不会有这种残留。
    /// </para>
    /// </summary>
    private void OnBallMouseDown(object sender, MouseButtonEventArgs e)
    {
        Log($"DOWN  left={Left:F0} top={Top:F0}");

        _dragStartLeft = Left;
        _dragStartTop = Top;
        _moved = false;

        e.Handled = true;

        try
        {
            // 阻塞到鼠标松开；期间窗口跟着鼠标走
            DragMove();
        }
        catch
        {
            // 极少数情况（窗口未激活等）会抛，忽略即可
        }

        // 走到这里说明鼠标已经松开了
        Log($"AFTER-DRAG  moved={_moved} left={Left:F0} top={Top:F0}");

        if (_moved)
        {
            return;   // 拖动过，不当成点击
        }

        Log("TOGGLE");
        Toggle();
    }

    // ---------------- 展开 / 收起 ----------------

    private void Toggle()
    {
        if (_expanded)
        {
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    private void Expand()
    {
        // 以当前球心为中心放大，球看起来"原地摊开"
        var centerX = Left + Width / 2;
        var centerY = Top + Height / 2;

        Width = ExpandedSize;
        Height = ExpandedSize;
        Left = centerX - ExpandedSize / 2;
        Top = centerY - ExpandedSize / 2;

        _expanded = true;
        Log($"EXPAND  w={Width} h={Height} left={Left} top={Top}");

        CompassLayer.Visibility = Visibility.Visible;
        CompassLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void Collapse()
    {
        var centerX = Left + Width / 2;
        var centerY = Top + Height / 2;

        Width = CollapsedSize;
        Height = CollapsedSize;
        Left = centerX - CollapsedSize / 2;
        Top = centerY - CollapsedSize / 2;

        _expanded = false;
        Log("COLLAPSE");

        CompassLayer.Visibility = Visibility.Collapsed;
        CompassLayer.Opacity = 0;
    }

    // ---------------- 罗盘上的功能 ----------------

    /// <summary>点名：直接出名字，然后收起，不打断放映。</summary>
    private void OnRollCallClick(object sender, RoutedEventArgs e)
    {
        RollCallResult? result;

        try
        {
            result = AppServices.RollCall.Roll();
        }
        catch (Exception ex)
        {
            ShowResultPopup("出错", ex.Message);
            Collapse();
            return;
        }

        if (result is null)
        {
            // 「只点未到」模式下全点完了
            ShowResultPopup(
                AppServices.RollCall.IsAllCalled ? "都点过了" : "没有名单",
                AppServices.RollCall.IsAllCalled ? null : "请先在主界面导入名单");

            Collapse();
            return;
        }

        // 通知点名页刷新「已点」标记
        AppServices.NotifyRollCallChanged();

        ShowResultPopup(
            result.Student.Name,
            result.AlreadyCalled ? "本轮已点过（重复）" : null);

        Collapse();
    }

    /// <summary>清空本轮记录（名单还在）。</summary>
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.RollCall.Reset();
            AppServices.NotifyRollCallChanged();
            ShowResultPopup("已复位", "点名记录已清空，名单还在");
        }
        catch
        {
            ShowResultPopup("复位失败", null);
        }

        Collapse();
    }

    /// <summary>把主窗口叫到前面。</summary>
    private void OnOpenMainClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Application.Current?.MainWindow is { } main)
            {
                if (main.WindowState == WindowState.Minimized)
                {
                    main.WindowState = WindowState.Normal;
                }

                main.Show();
                main.Activate();
            }
        }
        catch
        {
            // 主窗口没了就算了
        }

        Collapse();
    }

    private void OnCloseCompassClick(object sender, RoutedEventArgs e) => Collapse();

    // ---------------- 名字显示 ----------------

    /// <summary>
    /// 弹结果浮窗（大字，独立窗口浮在 PPT 上）。
    /// <para>球本身保持图标不动 —— 72px 里塞名字投影出来根本看不清。</para>
    /// </summary>
    private void ShowResultPopup(string name, string? hint)
    {
        try
        {
            _resultPopup?.Close();
            _resultPopup = new RollCallResultPopup(name, hint);
            _resultPopup.Show();
        }
        catch
        {
            // 浮窗起不来就让球闪一下，至少有个反馈
            try
            {
                BallIcon.Visibility = Visibility.Collapsed;
                BallName.Text = name;
                BallName.Visibility = Visibility.Visible;

                _nameTimer?.Stop();
                _nameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _nameTimer.Tick += (_, _) =>
                {
                    _nameTimer?.Stop();
                    BallName.Visibility = Visibility.Collapsed;
                    BallIcon.Visibility = Visibility.Visible;
                };
                _nameTimer.Start();
            }
            catch
            {
                // 忽略
            }
        }
    }

    // ---------------- 置顶加固 ----------------

    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>右键「隐藏悬浮球」：藏起来，用托盘图标可以再叫出来。</summary>
    private void OnHideBallClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Hide();
        }
        catch
        {
            // 忽略
        }
    }

    private void KeepOnTop()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;

            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        catch
        {
            // 拿不到句柄就算了，Topmost 属性还能顶
        }
    }

    /// <summary>
    /// 每 2 秒重新声明一次置顶。
    /// <para>
    /// PowerPoint 全屏放映、某些播放器会把自己的窗口提到最顶层，
    /// 单靠 Topmost 有可能被压下去一次；周期性重申成本极低。
    /// </para>
    /// </summary>
    private void StartTopmostGuard()
    {
        _topmostGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostGuard.Tick += (_, _) => KeepOnTop();
        _topmostGuard.Start();
    }
}
