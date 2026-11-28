using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClassManager.App.Views;

/// <summary>
/// 点名结果浮窗：和主界面「本次点名」那块同等观感，但独立浮在最上层。
/// <para>
/// <b>为什么单独开一个窗</b>：之前把名字塞进 72px 的悬浮球里，字只能缩到 10~17pt，
/// 投影到教室幕布上根本看不清。这里用 120pt 大字 + 自适配缩放的独立窗口。
/// </para>
/// <para>
/// 不抢焦点（ShowActivated=False），4.5 秒自动淡出，点一下也能立刻关掉。
/// </para>
/// </summary>
public partial class RollCallResultPopup : Window
{
    private readonly DispatcherTimer _timer;
    private bool _closing;

    public RollCallResultPopup(string name, string? hint = null)
    {
        InitializeComponent();

        NameText.Text = name ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        // 长名字（4 字以上）稍微收一点，避免 Viewbox 缩得太小
        var length = (name ?? string.Empty).Length;
        NameText.FontSize = length switch
        {
            <= 2 => 132,
            3 => 120,
            4 => 104,
            _ => 92,
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _timer.Tick += (_, _) => FadeOutAndClose();
        _timer.Start();

        // 淡入
        Opacity = 0;
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

            // 顶层但不抢焦点
            Topmost = true;
        };

        // 点一下立刻关（老师赶时间）
        MouseDown += (_, _) => FadeOutAndClose();
        KeyDown += (_, _) => FadeOutAndClose();
    }

    private void FadeOutAndClose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            try
            {
                Close();
            }
            catch
            {
                // 已经关了
            }
        };

        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>点标题栏以外的任何地方都算"知道了"。</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        FadeOutAndClose();
    }
}
