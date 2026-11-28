using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClassManager.App.ViewModels;
using ClassManager.App.Services;

namespace ClassManager.App.Views;

/// <summary>
/// AI 办公页：Enter 发送 / Shift+Enter 换行；消息区独立滚动，输入区是固定底栏。
/// 布局与 QQ / 微信一致：消息列表占满剩余高度并可滚动，输入框永远停在底部。
/// </summary>
public partial class AiOfficePage : Page
{
    private AiOfficeViewModel? _viewModel;

    /// <summary>合并滚动请求，避免每个 token 都触发一次滚动调度。</summary>
    private readonly DispatcherTimer _scrollTimer;

    public AiOfficePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // NavigationView 不给 Page 限高（实测页面被拉到 7744px），
        // 导致消息区没有滚动范围、底部输入框被推出可视区。
        Loaded += (_, _) => PageHeightBinder.Attach(this, Root);

        _scrollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _scrollTimer.Tick += (_, _) =>
        {
            _scrollTimer.Stop();
            MessageScroll.ScrollToEnd();
        };

        // 离开本模块就停掉，别让定时器继续跑
        Unloaded += (_, _) => _scrollTimer.Stop();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _viewModel = DataContext as AiOfficeViewModel;

        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            ScrollToEnd();
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<ChatItem>())
            {
                item.PropertyChanged += OnBubblePropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<ChatItem>())
            {
                item.PropertyChanged -= OnBubblePropertyChanged;
            }
        }

        ScrollToEnd();
    }

    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatItem.Content))
        {
            ScrollToEnd();
        }
    }

    /// <summary>
    /// 显式接管滚轮：某些嵌套容器会吞掉 MouseWheel，导致只能拖滚动条。
    /// 这里直接换算成偏移量，滚轮必定生效。
    /// </summary>
    private void OnMessagesPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MessageScroll.ScrollableHeight <= 0)
        {
            return;
        }

        var target = MessageScroll.VerticalOffset - e.Delta / 3.0;
        MessageScroll.ScrollToVerticalOffset(
            Math.Max(0, Math.Min(target, MessageScroll.ScrollableHeight)));

        e.Handled = true;
    }

    /// <summary>
    /// 滚到底部。流式输出时每个 token 都会触发一次，如果每次都调度 Dispatcher 操作，
    /// 会把消息队列堆满、让打字看起来很卡。这里用定时器合并成最多每 200ms 一次。
    /// </summary>
    private void ScrollToEnd()
    {
        if (!_scrollTimer.IsEnabled)
        {
            _scrollTimer.Start();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel?.AddAttachments(paths);
        }

        e.Handled = true;
    }

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 主键盘 Enter 是 Key.Enter，小键盘是 Key.Return，两个都要管
        if (e.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        // Shift+Enter / Ctrl+Enter 换行。
        // 这里**手动插入换行**而不是「放手让默认行为处理」——
        // ui:TextBox 在某些情况下会吞掉带修饰键的 Enter，
        // 交给默认行为的结果就是「按了没反应」。
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0)
        {
            if (sender is TextBox box)
            {
                var caret = box.CaretIndex;
                var newline = Environment.NewLine;

                box.Text = box.Text.Insert(caret, newline);
                box.CaretIndex = caret + newline.Length;
            }

            e.Handled = true;
            return;
        }

        e.Handled = true;

        if (_viewModel is { CanSend: true })
        {
            _viewModel.SendCommand.Execute(null);
        }
    }

    /// <summary>打开 Key 配置窗口；保存后刷新模型下拉（换了 key 可能连模型都不一样了）。</summary>
    private void OnConfigureKeyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new AiKeySettingsWindow { Owner = Window.GetWindow(this) };

            if (window.ShowDialog() == true)
            {
                _viewModel?.ReloadModels();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "打开设置窗口失败：" + ex.Message,
                "AI 服务设置",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }
}
