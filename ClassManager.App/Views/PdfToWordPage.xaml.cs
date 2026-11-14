using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;

namespace ClassManager.App.Views;

/// <summary>PDF 转 Word 页：选文件、跑转换、看日志，支持把 PDF 拖进来。</summary>
public partial class PdfToWordPage : Page
{
    public PdfToWordPage()
    {
        InitializeComponent();

        // NavigationView 不给 Page 限高，这里把页面钉到宿主高度（详见 PageHeightBinder）
        Loaded += (_, _) => PageHeightBinder.Attach(this, Root);
    }

    private PdfToWordViewModel? ViewModel => DataContext as PdfToWordViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetPdf(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = TryGetPdf(e);

        if (path is null)
        {
            return;
        }

        ViewModel?.SetPdf(path);
        e.Handled = true;
    }

    private static string? TryGetPdf(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return null;
        }

        return files.FirstOrDefault(f =>
            string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase)
            && File.Exists(f));
    }
}
