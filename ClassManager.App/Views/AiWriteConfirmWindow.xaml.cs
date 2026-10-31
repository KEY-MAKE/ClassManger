using System.Windows;

namespace ClassManager.App.Views;

/// <summary>
/// AI 写入确认窗口。
/// <para>
/// 为什么不用 MessageBox：WPF 的 MessageBox.Show 无法指定 owner，在 Windows 11 + Mica 窗口下
/// 经常弹到主窗口<b>后面</b>，用户看不见它但它在等待点击，界面就像卡死了一样。
/// 这里用自己的窗口：强制 Topmost + CenterOwner + Activate，一定能被看到。
/// 另外提供「本次会话都允许」，避免连续写多个文件时反复打断。
/// </para>
/// </summary>
public partial class AiWriteConfirmWindow : Window
{
    /// <summary>是否选择了「本次会话都允许」。</summary>
    public bool AllowForRestOfSession { get; private set; }

    /// <summary>用户是否允许。</summary>
    public bool Allowed { get; private set; }

    public AiWriteConfirmWindow(string fullPath, string action, string preview)
    {
        InitializeComponent();

        PathText.Text = fullPath;
        ActionText.Text = action;
        PreviewText.Text = string.IsNullOrWhiteSpace(preview) ? "（无预览内容）" : preview;

        Loaded += (_, _) =>
        {
            Activate();
            PreviewText.Focus();
        };
    }

    private void OnAllowClick(object sender, RoutedEventArgs e)
    {
        Allowed = true;
        DialogResult = true;
        Close();
    }

    private void OnAllowSessionClick(object sender, RoutedEventArgs e)
    {
        Allowed = true;
        AllowForRestOfSession = true;
        DialogResult = true;
        Close();
    }

    private void OnDenyClick(object sender, RoutedEventArgs e)
    {
        Allowed = false;
        DialogResult = false;
        Close();
    }
}
