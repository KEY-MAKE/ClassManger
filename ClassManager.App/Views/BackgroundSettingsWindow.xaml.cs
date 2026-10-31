using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>
/// 课件背景设置：选择背景图片并预览；可选「使用默认背景」（引擎自带的 slide_bg.png）。
/// </summary>
public partial class BackgroundSettingsWindow : FluentWindow
{
    private readonly string? _defaultImagePath;
    private string? _selectedPath;

    /// <summary>用户最终选择的背景图；null 表示使用默认背景。</summary>
    public string? SelectedPath => _selectedPath;

    /// <param name="currentPath">当前已保存的自定义背景（可为 null）。</param>
    /// <param name="defaultImagePath">引擎自带的默认背景图路径（可为 null，取不到就只显示提示）。</param>
    public BackgroundSettingsWindow(string? currentPath, string? defaultImagePath)
    {
        InitializeComponent();

        _defaultImagePath = defaultImagePath;
        _selectedPath = string.IsNullOrWhiteSpace(currentPath) ? null : currentPath;

        UpdatePreview();
    }

    private void OnChooseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
        };

        if (!string.IsNullOrWhiteSpace(_selectedPath) && File.Exists(_selectedPath))
        {
            var dir = Path.GetDirectoryName(_selectedPath);
            if (!string.IsNullOrEmpty(dir))
            {
                dialog.InitialDirectory = dir;
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            _selectedPath = dialog.FileName;
            UpdatePreview();
        }
    }

    private void OnUseDefaultClick(object sender, RoutedEventArgs e)
    {
        _selectedPath = null;
        UpdatePreview();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedPath) && !File.Exists(_selectedPath))
        {
            System.Windows.MessageBox.Show(this, "所选背景图已不存在，请重新选择。", "背景设置",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdatePreview()
    {
        var path = string.IsNullOrWhiteSpace(_selectedPath) ? _defaultImagePath : _selectedPath;

        SourceText.Text = string.IsNullOrWhiteSpace(_selectedPath)
            ? "当前使用：默认背景（引擎自带）"
            : $"当前使用：{Path.GetFileName(_selectedPath)}";

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = string.IsNullOrWhiteSpace(_selectedPath)
                ? "默认背景尚未就绪（首次使用需先解压引擎）"
                : "找不到该图片文件";
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
        }
        catch
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "该文件无法作为图片预览";
        }
    }
}
