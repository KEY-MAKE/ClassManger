using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassManager.App.Services;

/// <summary>
/// 控制台宿主工具：让 WinExe 在命令行场景（--selftest 等）也能输出到调用方控制台。
/// </summary>
public static class ConsoleHost
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    /// <summary>是否已经可用地向某个控制台输出（由 <see cref="Ensure"/> 或 <see cref="AttachOnly"/> 置位）。</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>确保标准输出可用（附加到父进程控制台，否则新建控制台）。仅用于 CLI 模式。</summary>
    public static void Ensure()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                AllocConsole();
            }

            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(writer);
            IsAvailable = true;
        }
        catch
        {
            // 无法附加控制台时忽略（退出码仍然可用）
        }
    }

    /// <summary>
    /// 仅尝试附加到父进程控制台，**绝不新建控制台窗口**。
    /// GUI 启动路径必须用它——否则 <see cref="Ensure"/> 的 AllocConsole 会让 WinExe 凭空弹出一个黑框。
    /// </summary>
    public static void AttachOnly()
    {
        if (IsAvailable)
        {
            return;
        }

        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                // 没有父控制台（用户双击启动）：保持静默，不要创建窗口
                return;
            }

            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(writer);
            IsAvailable = true;
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// GUI 路径专用的诊断输出：只有在已有控制台时才写，否则完全静默。
    /// </summary>
    public static void Log(string message)
    {
        if (!IsAvailable)
        {
            return;
        }

        WriteLine(message);
    }

    /// <summary>输出一行诊断信息。</summary>
    public static void WriteLine(string message)
    {
        try
        {
            Console.Out.WriteLine(message);
        }
        catch
        {
            // 忽略
        }
    }
}
