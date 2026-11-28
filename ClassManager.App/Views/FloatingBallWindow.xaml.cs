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
    private bool _dragging;
    private Point _grabOffset;
    private DispatcherTimer? _nameTimer;
    private DispatcherTimer? _topmostGuard;

    public FloatingBallWindow()
    {
        InitializeComponent();

        Width = CollapsedSize;
        Height = CollapsedSize;

        // 放在屏幕右下角，留一点边距
        var area = SystemParameters.WorkArea;
        Left = area.Right - CollapsedSize - 36;
        Top = area.Bottom - CollapsedSize - 90;

        Loaded += (_, _) =>
        {
            KeepOnTop();
            StartTopmostGuard();
        };

        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        };

        Closed += (_, _) =>
        {
            _nameTimer?.Stop();
            _topmostGuard?.Stop();
        };
    }

    // ---------------- 拖动 / 点击 ----------------

    private void OnBallMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 统一用「屏幕坐标」做基准：e.GetPosition 给的是逻辑像素，
        // PointToScreen 给的是物理像素，系统开了 125%/150% 缩放时两者不等，
        // 混着算就会出现「拖一下飞出去」或者「拖不动」。
        var screen = PointToScreen(e.GetPosition(this));
        _grabOffset = new Point(screen.X - Left, screen.Y - Top);

        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnBallMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !IsMouseCaptured)
        {
            return;
        }

        var now = PointToScreen(e.GetPosition(this));

        // 超过 3 像素才算拖动，避免手抖导致点不开罗盘
        if (!_dragging
            && (Math.Abs(now.X - Left - _grabOffset.X) > 3
                || Math.Abs(now.Y - Top - _grabOffset.Y) > 3))
        {
            _dragging = true;
        }

        if (!_dragging)
        {
            return;
        }

        Left = now.X - _grabOffset.X;
        Top = now.Y - _grabOffset.Y;
    }

    private void OnBallMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (_dragging)
        {
            _dragging = false;
            return;
        }

        // 左键单击（没拖动）→ 展开 / 收起罗盘
        Toggle();
        e.Handled = true;
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
            ShowName("出错：" + ex.Message);
            Collapse();
            return;
        }

        if (result is null)
        {
            // 「只点未到」模式下全点完了
            ShowName(AppServices.RollCall.IsAllCalled ? "都点过了" : "没有名单");
            Collapse();
            return;
        }

        // 通知点名页刷新「已点」标记
        AppServices.NotifyRollCallChanged();

        var name = result.Student.Name;

        if (result.AlreadyCalled)
        {
            name += "\n(重复)";
        }

        ShowName(name);
        Collapse();
    }

    /// <summary>清空本轮记录（名单还在）。</summary>
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.RollCall.Reset();
            AppServices.NotifyRollCallChanged();
            ShowName("已复位");
        }
        catch
        {
            ShowName("复位失败");
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

    private void ShowName(string name)
    {
        BallIcon.Visibility = Visibility.Collapsed;
        BallName.Text = name;

        // 名字长了就缩字号，尽量塞进球里
        BallName.FontSize = name.Length switch
        {
            <= 2 => 17,
            3 => 15,
            4 => 12.5,
            _ => 10.5,
        };

        BallName.Visibility = Visibility.Visible;

        _nameTimer?.Stop();
        _nameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _nameTimer.Tick += (_, _) =>
        {
            _nameTimer?.Stop();
            BallName.Visibility = Visibility.Collapsed;
            BallIcon.Visibility = Visibility.Visible;
        };
        _nameTimer.Start();
    }

    // ---------------- 置顶加固 ----------------

    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// 让「不该点的区域」鼠标穿透过去。
    /// <para>
    /// 透明窗口的命中测试是按矩形算的：72×72 的收起态、340×340 的展开态，
    /// 四角那些完全透明的地方照样会吃掉下面的点击 —— 用户会觉得「这块点不动了」。
    /// 这里按圆形判定，圆外一律返回 HTTRANSPARENT，鼠标事件直接穿到下层窗口。
    /// </para>
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        if (msg != WM_NCHITTEST)
        {
            return IntPtr.Zero;
        }

        try
        {
            // lParam 低 16 位是 x、高 16 位是 y（屏幕坐标，有符号）
            var raw = lParam.ToInt64();
            var screenX = (short)(raw & 0xFFFF);
            var screenY = (short)((raw >> 16) & 0xFFFF);

            // 注意：这里必须用 Width/Height（我们显式设过），
            // 不能用 ActualWidth/ActualHeight —— 窗口刚创建、还没布局时它们是 0，
            // 一算就变成「整个窗口都在圆外」，连球自己都被判成穿透，于是点不动也拖不动。
            var w = Width;
            var h = Height;

            if (double.IsNaN(w) || double.IsNaN(h) || w <= 1 || h <= 1)
            {
                // 尺寸还没准备好 → 不要穿透，宁可挡住也不能让球失去响应
                return IntPtr.Zero;
            }

            var local = PointFromScreen(new Point(screenX, screenY));

            // 球本身永远可点（半径给足，含光晕）
            var ballR = 36.0;
            var ballDx = local.X - w / 2;
            var ballDy = local.Y - h / 2;

            if (ballDx * ballDx + ballDy * ballDy <= ballR * ballR)
            {
                return IntPtr.Zero;   // 球上 → 正常处理
            }

            if (!_expanded)
            {
                // 收起态：球以外的四角穿透
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }

            // 展开态：盘面内可点，盘面外穿透
            var panelR = 152.0;
            var dx = local.X - w / 2;
            var dy = local.Y - h / 2;

            if (dx * dx + dy * dy > panelR * panelR)
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }
        }
        catch
        {
            // 算不出来就按默认（可点）处理，宁可挡住也不能让球点不了
        }

        return IntPtr.Zero;
    }

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
