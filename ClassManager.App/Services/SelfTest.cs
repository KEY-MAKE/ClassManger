using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>
/// 内部自检（无 UI，供 --selftest 使用）：完成组合根装配并验证关键路径。
/// 退出码：0 = 全部通过；1 = 存在失败项。
/// </summary>
public static class SelfTest
{
    /// <summary>输出安装状态（诊断用，不计入通过/失败）。</summary>
    private static void WriteInstallStatus()
    {
        try
        {
            var install = AppServices.Install;
            var status = install.GetStatus();

            if (status.State == InstallState.NotInstalled)
            {
                ConsoleHost.WriteLine("安装状态: 未安装");
                return;
            }

            ConsoleHost.WriteLine($"安装状态: {status.State}（注册表版本 v{status.InstalledVersion ?? "?"}，当前程序 {AppVersion.Display}）");
            ConsoleHost.WriteLine($"          位置: {status.InstallDir}");
            ConsoleHost.WriteLine($"          主程序: {(status.ExeLength > 0 ? $"{status.ExeLength:N0} 字节" : "缺失")}");
            ConsoleHost.WriteLine($"          说明: {status.Detail}");
        }
        catch (Exception ex)
        {
            ConsoleHost.WriteLine($"安装状态: 读取失败（{ex.Message}）");
        }
    }

    /// <summary>执行自检并返回退出码。</summary>
    public static int Run()
    {
        ConsoleHost.Ensure();

        ConsoleHost.WriteLine("ClassManager 自检开始...");
        ConsoleHost.WriteLine($"版本: {GetVersionString()}");

        try
        {
            AppServices.Initialize();
            ConsoleHost.WriteLine("  [通过] 组合根装配");
        }
        catch (Exception ex)
        {
            ConsoleHost.WriteLine($"  [失败] 组合根装配: {ex.Message}");
            ConsoleHost.WriteLine("自检结果: 失败");
            return ExitCodes.Error;
        }

        // 安装状态依赖组合根装配完成后再读（AppServices.Install 此时才非 null）
        WriteInstallStatus();

        var failures = new List<string>();

        Check("设置路径与序列化往返", () =>
        {
            if (!AppServices.Settings.SettingsFilePath.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("设置文件路径不符合预期");
            }

            var sample = new AppSettings
            {
                Theme = ThemePreference.Dark,
                GenerateDemoScores = false,
                WindowWidth = 1234,
            };

            var json = JsonSerializer.Serialize(sample, SettingsService.JsonOptions);
            var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions)
                ?? throw new InvalidOperationException("反序列化为空");

            if (back.Theme != ThemePreference.Dark || back.GenerateDemoScores || Math.Abs(back.WindowWidth - 1234) > 0.001)
            {
                throw new InvalidOperationException("往返结果不一致");
            }
        }, failures);

        Check("导入服务自检", () =>
        {
            var service = new ImportService();
            var rows = new List<IDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["姓名"] = "张三", ["成绩"] = 88 },
                new Dictionary<string, object?> { ["姓名"] = "李四", ["成绩"] = 95 },
                new Dictionary<string, object?> { ["姓名"] = "王五", ["成绩"] = 72 },
            };

            var result = service.ImportFromRows(rows, "自检班", true);
            if (result.Students.Count != 3)
            {
                throw new InvalidOperationException($"期望 3 名学生，实际 {result.Students.Count}");
            }

            if (result.Students[0].Name != "李四")
            {
                throw new InvalidOperationException("未按成绩降序排序");
            }
        }, failures);

        Check("排座算法跑一遍", () =>
        {
            var seating = new SeatingService();
            var roster = new List<Student>
            {
                new("张三", 88), new("李四", 95), new("王五", 72), new("赵六", 60),
                new("钱七", 81), new("孙八", 90), new("周九", 66), new("吴十", 77),
            };
            seating.LoadRoster(roster);
            seating.UpdateLayout(6, 8);

            foreach (var algorithm in new[] { SeatAlgorithm.Snake, SeatAlgorithm.Grouped, SeatAlgorithm.Random })
            {
                seating.ArrangeAuto(algorithm);
                if (seating.Layout.OccupiedSeats != roster.Count)
                {
                    throw new InvalidOperationException($"{algorithm} 排列后占用座位数不正确");
                }
            }
        }, failures);

        Check("导出 xlsx 双 sheet", () =>
        {
            var seating = new SeatingService();
            seating.LoadRoster(new List<Student> { new("张三", 88), new("李四", 95) });
            seating.UpdateLayout(3, 4);
            seating.ArrangeAuto(SeatAlgorithm.Snake);

            var path = Path.Combine(Path.GetTempPath(), $"classmanager_selftest_{Environment.ProcessId}.xlsx");

            try
            {
                AppServices.Export.ExportSeatXlsx(path, seating.Layout, "自检班", DateTime.Now);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    throw new InvalidOperationException("xlsx 未生成");
                }
            }
            finally
            {
                try
                {
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
        }, failures);

        Check("单实例互斥可获取", () =>
        {
            using var mutex = new Mutex(false, @"Global\ClassManager.SelfTestProbe");
            if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
            {
                throw new InvalidOperationException("无法获取自检互斥量");
            }

            mutex.ReleaseMutex();
        }, failures);

        if (failures.Count == 0)
        {
            ConsoleHost.WriteLine("自检结果: 通过");
            return ExitCodes.Ok;
        }

        ConsoleHost.WriteLine($"自检结果: 失败（{failures.Count} 项）");
        foreach (var name in failures)
        {
            ConsoleHost.WriteLine($"  - 未通过: {name}");
        }

        return ExitCodes.Error;

        void Check(string name, Action action, ICollection<string> errors)
        {
            try
            {
                action();
                ConsoleHost.WriteLine($"  [通过] {name}");
            }
            catch (Exception ex)
            {
                ConsoleHost.WriteLine($"  [失败] {name}: {ex.Message}");
                errors.Add(name);
            }
        }
    }

    /// <summary>读取程序集版本字符串。</summary>
    public static string GetVersionString()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SelfTest).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return info;
        }

        return assembly.GetName().Version?.ToString() ?? "1.0.0";
    }
}
