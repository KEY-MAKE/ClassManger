using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ClassManager.App.Services;

/// <summary>
/// 独立卸载清理器（由临时目录中的程序副本执行，命令：`--purge &lt;pid&gt; &lt;installDir&gt; [removeUserSettings]`）。
/// 之所以要有它：执行删除的进程不能运行在待删目录里，否则文件被自己锁住，
/// 表现为「点了卸载但目录删不干净」。
/// </summary>
public static class UninstallPurge
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClassManager";
    private const string ShortcutName = "班级综合管理系统.lnk";

    /// <summary>等待主进程退出的上限（毫秒）。</summary>
    private const int WaitProcessBudgetMs = 120_000;

    /// <summary>删除安装目录的时间预算（毫秒）。</summary>
    private const int DeleteBudgetMs = 180_000;

    /// <summary>执行清理并返回退出码。</summary>
    public static int Run(int mainPid, string installDir, bool removeUserSettings)
    {
        ConsoleHost.Ensure();
        ConsoleHost.WriteLine("ClassManager 卸载清理开始...");
        AppendLog($"卸载清理开始：安装目录={installDir}，删除用户设置={removeUserSettings}");

        // 1) 等待主进程退出（进程已不存在则立即继续）
        WaitProcessExit(mainPid);
        ConsoleHost.WriteLine("主进程已退出。");

        // 2) 快捷方式
        DeleteShortcut(Environment.SpecialFolder.DesktopDirectory);
        DeleteShortcut(Environment.SpecialFolder.Programs);
        ConsoleHost.WriteLine("快捷方式已清理。");

        // 3) 注册表卸载项
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
        }
        catch
        {
            // 忽略：项可能已不存在
        }

        ConsoleHost.WriteLine("卸载注册表项已清理。");

        // 4) 用户设置
        if (removeUserSettings)
        {
            var settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClassManager");
            if (Directory.Exists(settingsDir))
            {
                TryDeleteWithBudget(settingsDir, 5_000);
                ConsoleHost.WriteLine(Directory.Exists(settingsDir)
                    ? "警告：用户设置目录未能完全删除。"
                    : "用户设置目录已删除。");
            }
        }

        // 5) 安装目录（带时间预算的重试：先尽力清空内容再删目录）
        //
        // 【安全闸】这里拿到的路径来自命令行。真实事故：用户在 Downloads 里双击 exe，
        // 注册表无记录 → 旧代码把「当前程序所在目录」当成安装目录 → 这一行
        // 递归删光了整个下载夹（用户数据永久丢失，不进回收站）。
        // 因此删除前必须确认它确实像本程序的安装目录，否则宁可不动。
        var safeToDelete = InstallService.IsSafeUninstallTarget(installDir, out var unsafeReason);

        if (!safeToDelete)
        {
            ConsoleHost.WriteLine($"跳过安装目录删除：{unsafeReason}");
            ConsoleHost.WriteLine($"  路径：{installDir}");
            ConsoleHost.WriteLine("  如需清理，请手动删除该目录（或重新安装后再卸载）。");
            AppendLog($"卸载清理：跳过安装目录删除（{unsafeReason}），installDir={installDir}");
        }

        var cleaned = !safeToDelete
                      || string.IsNullOrWhiteSpace(installDir)
                      || !Directory.Exists(installDir)
                      || TryDeleteWithBudget(installDir, DeleteBudgetMs);

        if (safeToDelete)
        {
            ConsoleHost.WriteLine(cleaned
                ? $"安装目录已彻底删除: {installDir}"
                : $"警告：安装目录未能完全删除: {installDir}");

            AppendLog(cleaned
                ? $"卸载清理：安装目录已删除 {installDir}"
                : $"卸载清理：安装目录未能完全删除 {installDir}");
        }

        // 6) 自身所在的临时目录（含自身 exe）——自身正在运行，必须延迟到退出后再删
        var selfDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!PathsEqual(selfDir, installDir))
        {
            ScheduleSelfDirRemoval(selfDir);
        }

        ConsoleHost.WriteLine("卸载清理结束。");
        return cleaned ? ExitCodes.Ok : ExitCodes.Error;
    }

    /// <summary>把清理动作写进安装日志（与 InstallService 共用同一个文件，便于回溯）。</summary>
    private static void AppendLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClassManager");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "install.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [purge] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }
    }

    private static void WaitProcessExit(int pid)
    {
        var waited = 0;
        while (waited < WaitProcessBudgetMs)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return;
                }
            }
            catch
            {
                // 进程不存在 → 立即继续
                return;
            }

            Thread.Sleep(300);
            waited += 300;
        }
    }

    private static void DeleteShortcut(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), ShortcutName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>在时间预算内反复尝试删除目录（先清空内容），返回是否删净。</summary>
    private static bool TryDeleteWithBudget(string dir, int budgetMs)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < budgetMs)
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }

            try
            {
                ClearDirectoryContents(dir);
                Directory.Delete(dir, true);
            }
            catch
            {
                // 文件被占用等 → 稍后重试
            }

            if (!Directory.Exists(dir))
            {
                return true;
            }

            Thread.Sleep(400);
        }

        return !Directory.Exists(dir);
    }

    private static void ClearDirectoryContents(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch
                {
                    // 单个文件失败不影响整体
                }
            }

            // 先删最深层目录
            var dirs = Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                               .OrderByDescending(d => d.Length)
                               .ToList();
            foreach (var sub in dirs)
            {
                try
                {
                    Directory.Delete(sub, true);
                }
                catch
                {
                    // 忽略
                }
            }
        }
        catch
        {
            // 枚举失败 → 交给上层重试
        }
    }

    /// <summary>
    /// 延迟删除自身所在临时目录：进程仍在运行、exe 被自己占用，
    /// 因此交给一个脱离本树的 PowerShell 子进程在本进程退出后删除。
    /// 采用 -EncodedCommand（UTF-16LE + Base64），命令行为纯 ASCII，路径可含任意字符。
    /// </summary>
    private static void ScheduleSelfDirRemoval(string selfDir)
    {
        try
        {
            var escaped = selfDir.Replace("'", "''");
            var script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                "Start-Sleep -Milliseconds 800; " +
                $"$dir='{escaped}'; " +
                "$i=0; " +
                "while (($i -lt 60) -and (Test-Path -LiteralPath $dir)) { " +
                "Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue; " +
                "if (Test-Path -LiteralPath $dir) { Start-Sleep -Milliseconds 300 }; " +
                "$i++ }";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
            // 尽力而为
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
