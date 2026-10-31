using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>
/// 座位表打印窗口：预览「白底黑格」矢量座位表，并可经 <see cref="System.Windows.Controls.PrintDialog"/>
/// 打印（矢量输出，可配合「Microsoft Print to PDF」导出 PDF）；按可用打印区域等比缩放居中。
/// </summary>
public partial class SeatPrintWindow : FluentWindow
{
    private readonly IExportService _export;
    private readonly SeatLayout _layout;
    private readonly string _className;
    private readonly bool _includeHeaders;
    private readonly bool _includeClassName;

    /// <summary>座位表可视化对象（打印时复用，保持矢量）。</summary>
    private readonly DrawingVisual _visual;

    private readonly Size _visualSize;

    public SeatPrintWindow(
        IExportService export,
        SeatLayout layout,
        string className,
        bool includeHeaders,
        bool includeClassName)
    {
        _export = export;
        _layout = layout;
        _className = className;
        _includeHeaders = includeHeaders;
        _includeClassName = includeClassName;

        InitializeComponent();

        var sheet = _export.BuildSeatSheet(_layout, _className, _includeHeaders, _includeClassName);
        _visual = sheet.Visual;
        _visualSize = sheet.Size;

        PreviewImage.Source = RenderPreview(_visual, _visualSize);
    }

    private static BitmapSource RenderPreview(DrawingVisual visual, Size size)
    {
        const double scale = 1.5d;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * scale),
            (int)Math.Ceiling(size.Height * scale),
            96d * scale,
            96d * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Controls.PrintDialog();

        // 座位表矢量按横向 A4 设计：打开打印对话框「之前」先预设纸张与方向，
        // 使用户一打开就默认看到 A4 横向（否则跟随驱动默认的纵向，打出的 PDF 也是纵向）。
        // 注意：.NET 的 System.Printing 中方向是 PrintTicket.PageOrientation，
        // 纸张规格是 PrintTicket.PageMediaSize（枚举 PageMediaSizeName.ISOA4），没有 PageSize/PageSizeKind。
        try
        {
            dialog.PrintTicket = new PrintTicket
            {
                PageOrientation = PageOrientation.Landscape,
                PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4),
            };
        }
        catch
        {
            // 部分打印机/驱动不支持预设票据，下面还有一次强制设置兜底
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 防止用户在对话框里改回纵向、或驱动返回只读票据：
        // 克隆一份可写票据后整体替换，这样即使原票据来自 PrintQueue.DefaultPrintTicket（只读）也能生效。
        // 仍在读取 PrintableAreaWidth/Height 之前执行，否则缩放会按纵向尺寸计算。
        try
        {
            var ticket = dialog.PrintTicket.Clone();
            ticket.PageOrientation = PageOrientation.Landscape;
            ticket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
            dialog.PrintTicket = ticket;
        }
        catch
        {
            // Clone 不可用时退化为直接修改，再失败则保持驱动默认值，不阻断打印
            try
            {
                dialog.PrintTicket.PageOrientation = PageOrientation.Landscape;
                dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
            }
            catch
            {
                // 忽略
            }
        }

        var areaWidth = dialog.PrintableAreaWidth;
        var areaHeight = dialog.PrintableAreaHeight;

        // 矢量图，放大不会糊；与导出同逻辑：等比缩放到填满可打印区域
        var scale = Math.Min(areaWidth / _visualSize.Width, areaHeight / _visualSize.Height);

        var offsetX = (areaWidth - _visualSize.Width * scale) / 2d;
        var offsetY = (areaHeight - _visualSize.Height * scale) / 2d;

        var content = new ContainerVisual();
        content.Transform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(scale, scale),
                new TranslateTransform(offsetX, offsetY),
            },
        };
        content.Children.Add(_visual);

        dialog.PrintVisual(content, "座位表");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
