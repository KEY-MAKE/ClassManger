using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;

namespace ClassManager.App.Views;

/// <summary>证件照页：选照片、选规格与底色、一键出图，支持把图片拖进来。</summary>
public partial class IdPhotoPage : Page
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    private static void Diag(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm_idphoto.log");
            System.IO.File.AppendAllText(
                path,
                $"{DateTime.Now:HH:mm:ss.fff} [页面] {message}\n",
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }

    public IdPhotoPage()
    {
        Diag("构造开始");
        InitializeComponent();
        Diag("InitializeComponent 完成");

        // NavigationView 不给 Page 限高，这里把页面钉到宿主高度（详见 PageHeightBinder）
        Loaded += (_, _) =>
        {
            Diag("Loaded，开始 Attach");
            PageHeightBinder.Attach(this, Root);
            Diag("Attach 完成");
        };

        Unloaded += (_, _) => Diag("Unloaded");
    }

    private IdPhotoViewModel? ViewModel => DataContext as IdPhotoViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetImage(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = TryGetImage(e);

        if (path is null)
        {
            return;
        }

        ViewModel?.SetSource(path);
        e.Handled = true;
    }

    private static string? TryGetImage(DragEventArgs e)
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
            ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
            && File.Exists(f));
    }
}
