using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClassManager.App.Services;

/// <summary>窗口激活实现：P/Invoke ShowWindow + SetForegroundWindow。</summary>
public sealed class WindowActivationService : IWindowActivationService
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <inheritdoc />
    public void BringToFront(Window window)
    {
        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
        }

        // 短暂置顶以获得焦点，再取消置顶
        window.Topmost = true;
        window.Topmost = false;
    }
}
