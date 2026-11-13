using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ClassManager.App.Models;
using ClassManager.App.Services;

namespace ClassManager.App.Controls;

/// <summary>座位点击事件参数。</summary>
public sealed class SeatClickEventArgs : EventArgs
{
    public SeatClickEventArgs(int row, int col)
    {
        Row = row;
        Col = col;
    }

    public int Row { get; }

    public int Col { get; }
}

/// <summary>学生被拖到座位上的事件参数。</summary>
public sealed class SeatDropEventArgs : EventArgs
{
    public SeatDropEventArgs(int row, int col, string studentName)
    {
        Row = row;
        Col = col;
        StudentName = studentName;
    }

    public int Row { get; }

    public int Col { get; }

    public string StudentName { get; }
}

/// <summary>
/// 极简座位画布：以单个矩形表示一个座位（已排座 / 空座 / 选中三种样式），
/// 座位内只显示学生姓名；底部为讲台，左侧排标签、顶部分组表头均可开关。
/// 画布固定为蓝白配色（不随明暗主题），并置于双向滚动容器内，超大可滚动 / 平移。
/// </summary>
public partial class SeatMapControl : UserControl
{
    private bool _panning;
    private Point _panStart;
    private double _panHorizontal;
    private double _panVertical;

    public SeatMapControl()
    {
        InitializeComponent();

        // 屏幕上的画布底色跟随主题：深色模式下用深灰，浅色模式下用原来的白。
        //
        // 注意：这里的底色**只影响屏幕显示**。导出图片 / 打印走的是
        // ExportService.BuildSeatSheet（独立绘制，用自己的白底），
        // 所以打印出来仍然是白纸黑字，不受主题影响。
        ApplyCanvasBackground();

        // 接收「学生名单」窗口拖过来的名字
        AllowDrop = true;
        DragOver += OnDragOverSeat;
        Drop += OnDropSeat;

        Loaded += (_, _) =>
        {
            ApplyCanvasBackground();
            SubscribeTheme();
            Redraw();
        };

        Unloaded += (_, _) => UnsubscribeTheme();
    }

    /// <summary>把画布底色刷成当前主题对应的颜色。</summary>
    private void ApplyCanvasBackground()
    {
        var brush = new SolidColorBrush(CurrentCanvasColor());
        Paper.Background = brush;
        Surface.Background = brush;
    }

    /// <summary>当前主题下画布该用的底色。</summary>
    private static Color CurrentCanvasColor()
    {
        try
        {
            var preference = AppServices.Theme.Preference;

            var dark = preference == ThemePreference.Dark
                       || (preference == ThemePreference.System
                           && Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                              == Wpf.Ui.Appearance.ApplicationTheme.Dark);

            return dark
                ? Color.FromRgb(0x1B, 0x1B, 0x1F)
                : SeatMapPalette.CanvasBackground;
        }
        catch
        {
            // 服务还没初始化（设计器 / 早期构造）→ 回退白底
            return SeatMapPalette.CanvasBackground;
        }
    }

    private void SubscribeTheme()
    {
        try
        {
            AppServices.Theme.Changed -= OnThemeChanged;
            AppServices.Theme.Changed += OnThemeChanged;
        }
        catch
        {
            // 忽略
        }
    }

    private void UnsubscribeTheme()
    {
        try
        {
            AppServices.Theme.Changed -= OnThemeChanged;
        }
        catch
        {
            // 忽略
        }
    }

    private void OnThemeChanged(object? sender, ThemePreference preference) => ApplyCanvasBackground();

    /// <summary>布局版本号：自增触发重绘。</summary>
    public static readonly DependencyProperty LayoutVersionProperty =
        DependencyProperty.Register(
            nameof(LayoutVersion),
            typeof(int),
            typeof(SeatMapControl),
            new PropertyMetadata(0, OnVisualPropertyChanged));

    /// <summary>当前选中的学生姓名（用于高亮其座位）。</summary>
    public static readonly DependencyProperty SelectedStudentNameProperty =
        DependencyProperty.Register(
            nameof(SelectedStudentName),
            typeof(string),
            typeof(SeatMapControl),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public int LayoutVersion
    {
        get => (int)GetValue(LayoutVersionProperty);
        set => SetValue(LayoutVersionProperty, value);
    }

    public string? SelectedStudentName
    {
        get => (string?)GetValue(SelectedStudentNameProperty);
        set => SetValue(SelectedStudentNameProperty, value);
    }

    /// <summary>座位被点击时触发。</summary>
    public event EventHandler<SeatClickEventArgs>? SeatClicked;

    /// <summary>学生名字被拖到某个座位上（跨窗口拖放）。</summary>
    public event EventHandler<SeatDropEventArgs>? SeatDropped;

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SeatMapControl control)
        {
            control.Redraw();
        }
    }

    /// <summary>依据当前布局重绘全部元素。</summary>
    public void Redraw()
    {
        if (Surface is null)
        {
            return;
        }

        Surface.Children.Clear();

        var layout = AppServices.Seating.Layout;
        var showHeaders = layout.ShowHeaders;

        var size = SeatMapGeometry.CanvasSize(layout, showHeaders);
        Surface.Width = size.Width;
        Surface.Height = size.Height;

        var xs = SeatMapGeometry.ColumnOffsets(layout, showHeaders);
        var ys = SeatMapGeometry.RowOffsets(layout, showHeaders);

        if (showHeaders)
        {
            DrawColumnHeaders(layout, xs);
            DrawRowLabels(layout, ys);
        }

        DrawPlatform(layout, showHeaders);

        // 讲台左右两个侧位（可坐人，复用同一套绘制/配色/命中逻辑）
        var platformSeats = SeatMapGeometry.PlatformSeats(layout, showHeaders);
        DrawSeat(
            layout,
            SeatLayout.PlatformSeatRow,
            SeatLayout.PlatformSeatLeftCol,
            platformSeats.Left.X,
            platformSeats.Left.Y);
        DrawSeat(
            layout,
            SeatLayout.PlatformSeatRow,
            SeatLayout.PlatformSeatRightCol,
            platformSeats.Right.X,
            platformSeats.Right.Y);

        for (var row = 0; row < layout.Rows; row++)
        {
            for (var col = 0; col < layout.Cols; col++)
            {
                DrawSeat(layout, row, col, xs[col], ys[row]);
            }
        }
    }

    /// <summary>绘制单个座位矩形（仅此一个图形）+ 姓名。</summary>
    private void DrawSeat(SeatLayout layout, int row, int col, double x, double y)
    {
        var student = layout.Get(row, col);
        var isSelected = student is not null
                         && !string.IsNullOrEmpty(SelectedStudentName)
                         && string.Equals(student, SelectedStudentName, StringComparison.Ordinal);

        Color fill;
        Color border;
        double thickness;
        if (isSelected)
        {
            fill = SeatMapPalette.SeatSelectedFill;
            border = SeatMapPalette.AccentBlue;
            thickness = 2d;
        }
        else if (student is not null)
        {
            fill = SeatMapPalette.SeatOccupiedFill;
            border = SeatMapPalette.SeatBorder;
            thickness = 1d;
        }
        else
        {
            fill = SeatMapPalette.SeatEmptyFill;
            border = SeatMapPalette.SeatBorder;
            thickness = 1d;
        }

        var rect = new Rectangle
        {
            Width = SeatMapGeometry.SeatWidth,
            Height = SeatMapGeometry.SeatHeight,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(border),
            StrokeThickness = thickness,
            SnapsToDevicePixels = true,
        };

        rect.Tag = new SeatPosition(row, col);
        rect.Cursor = Cursors.Hand;
        rect.MouseLeftButtonDown += OnSeatMouseDown;
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        Surface.Children.Add(rect);

        // 座位内只保留学生姓名（无分数、无「空座位」文字）。
        if (student is not null)
        {
            AddText(
                student,
                x + SeatMapGeometry.SeatWidth / 2d,
                y + SeatMapGeometry.SeatHeight / 2d,
                12d,
                FontWeights.Normal,
                new SolidColorBrush(SeatMapPalette.NameText),
                SeatMapGeometry.SeatWidth - 12d);
        }
    }

    /// <summary>绘制列表头：每一列一个「第x列」（不再按间隙切「组」）。</summary>
    private void DrawColumnHeaders(SeatLayout layout, double[] xs)
    {
        var centerY = SeatMapGeometry.MarginY(true) / 2d;
        var brush = new SolidColorBrush(SeatMapPalette.LabelText);

        for (var col = 0; col < layout.Cols; col++)
        {
            AddText(
                $"第{col + 1}列",
                xs[col] + SeatMapGeometry.SeatWidth / 2d,
                centerY,
                12d,
                FontWeights.Normal,
                brush);
        }
    }

    /// <summary>绘制左侧排标签（第一排在最下方）。</summary>
    private void DrawRowLabels(SeatLayout layout, double[] ys)
    {
        var rightX = SeatMapGeometry.MarginX(true) - 8d;
        var brush = new SolidColorBrush(SeatMapPalette.LabelText);

        for (var row = 0; row < layout.Rows; row++)
        {
            AddTextRight(
                $"第{row + 1}排",
                rightX,
                ys[row] + SeatMapGeometry.SeatHeight / 2d,
                12d,
                brush);
        }
    }

    /// <summary>绘制底部讲台（白底黑框黑字）。</summary>
    private void DrawPlatform(SeatLayout layout, bool showHeaders)
    {
        var rect = SeatMapGeometry.Platform(layout, showHeaders);

        var shape = new Rectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            Fill = new SolidColorBrush(SeatMapPalette.PlatformFill),
            Stroke = new SolidColorBrush(SeatMapPalette.PlatformBorder),
            StrokeThickness = 1.5d,
            SnapsToDevicePixels = true,
        };

        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        Surface.Children.Add(shape);

        AddText(
            "讲台",
            rect.X + rect.Width / 2d,
            rect.Y + rect.Height / 2d,
            14d,
            FontWeights.Bold,
            new SolidColorBrush(SeatMapPalette.PlatformText));
    }

    private void OnSeatMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (sender is FrameworkElement { Tag: SeatPosition position })
        {
            SeatClicked?.Invoke(this, new SeatClickEventArgs(position.Row, position.Col));
            e.Handled = true;
        }
    }

    private void OnDragOverSeat(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropSeat(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text))
        {
            return;
        }

        var name = (e.Data.GetData(DataFormats.Text) as string)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var position = HitTestSeat(e.GetPosition(Surface));
        if (position is null)
        {
            return;
        }

        SeatDropped?.Invoke(this, new SeatDropEventArgs(position.Value.Row, position.Value.Col, name));
        e.Handled = true;
    }

    /// <summary>按坐标找座位（座位矩形自带 SeatPosition 标签）。</summary>
    private SeatPosition? HitTestSeat(Point point)
    {
        foreach (var child in Surface.Children)
        {
            if (child is FrameworkElement { Tag: SeatPosition position } element)
            {
                var bounds = new Rect(
                    Canvas.GetLeft(element),
                    Canvas.GetTop(element),
                    element.Width,
                    element.Height);

                if (bounds.Contains(point))
                {
                    return position;
                }
            }
        }

        return null;
    }

    /// <summary>添加居中的文本块（可选限定宽度以自动折行）。</summary>
    private void AddText(
        string text,
        double centerX,
        double centerY,
        double fontSize,
        FontWeight weight,
        Brush brush,
        double? maxWidth = null)
    {
        var block = CreateTextBlock(text, fontSize, weight, brush);
        if (maxWidth.HasValue)
        {
            block.Width = maxWidth.Value;
        }

        // 先测量以便居中
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = block.DesiredSize;

        Canvas.SetLeft(block, centerX - size.Width / 2d);
        Canvas.SetTop(block, centerY - size.Height / 2d);
        Surface.Children.Add(block);
    }

    /// <summary>添加右对齐（右边缘贴齐 rightX）且垂直居中的文本块。</summary>
    private void AddTextRight(
        string text,
        double rightX,
        double centerY,
        double fontSize,
        Brush brush)
    {
        var block = CreateTextBlock(text, fontSize, FontWeights.Normal, brush);
        block.TextAlignment = TextAlignment.Right;

        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = block.DesiredSize;

        Canvas.SetLeft(block, rightX - size.Width);
        Canvas.SetTop(block, centerY - size.Height / 2d);
        Surface.Children.Add(block);
    }

    private static TextBlock CreateTextBlock(string text, double fontSize, FontWeight weight, Brush brush) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontSize = fontSize,
        FontWeight = weight,
        Foreground = brush,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        IsHitTestVisible = false,
    };

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 滚轮：默认垂直滚动；按住 Shift 时水平滚动。
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return;
        }

        Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(this);
        _panHorizontal = Scroll.HorizontalOffset;
        _panVertical = Scroll.VerticalOffset;
        CaptureMouse();
        Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var current = e.GetPosition(this);
        Scroll.ScrollToHorizontalOffset(_panHorizontal - (current.X - _panStart.X));
        Scroll.ScrollToVerticalOffset(_panVertical - (current.Y - _panStart.Y));
        e.Handled = true;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning || e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _panning = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }
}
