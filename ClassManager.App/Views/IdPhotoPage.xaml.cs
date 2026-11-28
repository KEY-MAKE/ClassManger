using System.Collections.Generic;
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
        e.Effects = TryGetImages(e).Count == 0 ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var paths = TryGetImages(e);

        if (paths.Count == 0)
        {
            return;
        }

        // 一次拖进来的所有图片都收下，不再只取第一张
        ViewModel?.AppendSources(paths);
        e.Handled = true;
    }

    private static List<string> TryGetImages(DragEventArgs e)
    {
        var result = new List<string>();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return result;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return result;
        }

        foreach (var file in files)
        {
            if (ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                && File.Exists(file))
            {
                result.Add(file);
            }
        }

        return result;
    }
}
