using System.IO;
using System.Windows;
using ClassManager.App.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClassManager.App.Views;

/// <summary>
/// 更新源设置：填 version.json 的地址（http 直链或本地/局域网路径），可测试连接，
/// 并控制「启动时自动检查」与「忽略某个版本」。
/// </summary>
public partial class UpdateSourceWindow : FluentWindow
{
    private readonly IUpdateService _update;

    public UpdateSourceWindow(IUpdateService update)
    {
        InitializeComponent();

        _update = update;

        // 没配过更新源时，如果默认的本地更新源已经存在（用 --publish-update 生成过），
        // 直接预填好，用户不用自己去找路径。
        var source = update.Settings.SourceUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(source) && UpdatePublisher.HasDefaultSource)
        {
            source = UpdatePublisher.DefaultManifestPath;
        }

        SourceBox.Text = source;
        AutoCheckBox.IsChecked = update.Settings.AutoCheckOnStartup;

        RefreshIgnore();

        if (!string.IsNullOrWhiteSpace(update.Settings.LastCheckedText))
        {
            TestResultText.Text = "上次检查：" + update.Settings.LastCheckedText;
        }
    }

    private void OnSourceChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        TestResultText.Text = string.Empty;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 version.json",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        var current = SourceBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(current) && !current.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                dialog.InitialDirectory = dir;
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            SourceBox.Text = dialog.FileName;
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        ApplyToSettings();

        TestProgress.Visibility = Visibility.Visible;
        TestResultText.Text = "正在连接更新源…";

        var result = await _update.CheckAsync();

        TestProgress.Visibility = Visibility.Collapsed;

        if (!result.Ok)
        {
            TestResultText.Text = "✗ " + result.Message;
            return;
        }

        if (result.NotConfigured)
        {
            TestResultText.Text = "请先填写更新源地址。";
            return;
        }

        TestResultText.Text = result.HasUpdate
            ? $"✓ {result.Message}（当前 {AppVersion.Display}）"
            : $"✓ 连接正常 — {result.Message}";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ApplyToSettings();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnClearIgnoreClick(object sender, RoutedEventArgs e)
    {
        _update.Settings.IgnoredVersion = string.Empty;
        _update.Save();
        RefreshIgnore();
        TestResultText.Text = "已取消忽略，下次检查会重新提示该版本。";
    }

    private void ApplyToSettings()
    {
        _update.Settings.SourceUrl = SourceBox.Text?.Trim() ?? string.Empty;
        _update.Settings.AutoCheckOnStartup = AutoCheckBox.IsChecked ?? true;
        _update.Save();
    }

    private void RefreshIgnore()
    {
        var ignored = _update.Settings.IgnoredVersion ?? string.Empty;

        if (ignored.Length == 0)
        {
            IgnoreText.Visibility = Visibility.Collapsed;
            ClearIgnoreButton.Visibility = Visibility.Collapsed;
            return;
        }

        IgnoreText.Text = $"当前已忽略版本：v{ignored}（不会再提示更新到该版本）";
        IgnoreText.Visibility = Visibility.Visible;
        ClearIgnoreButton.Visibility = Visibility.Visible;
    }
}
