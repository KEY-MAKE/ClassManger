using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ClassManager.App.Models;
using Microsoft.Win32;

namespace ClassManager.App.Services;

/// <summary>
/// 安装 / 卸载实现：复制当前目录 → 安装目录；创建快捷方式；写入 HKCU 卸载项；
/// 卸载时删除目录 + 快捷方式 + 注册表并调度自删除脚本。
/// </summary>
public sealed class InstallService : IInstallService
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClassManager";
    private const string ExeName = "ClassManager.App.exe";
    private const string ShortcutName = "班级综合管理系统.lnk";
    private const string AppDisplayName = "班级综合管理系统";

    private readonly IShortcutService _shortcut;
    private readonly Func<AuthorInfo> _authorProvider;

    public InstallService(IShortcutService shortcut, Func<AuthorInfo> authorProvider)
    {
        _shortcut = shortcut;
        _authorProvider = authorProvider;
    }

    /// <inheritdoc />
    public string CurrentProcessDirectory =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <inheritdoc />
    public string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "ClassManager");

    /// <inheritdoc />
    public string InstallLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClassManager",
        "install.log");

    /// <inheritdoc />
    public string? InstalledVersion
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
                return key?.GetValue("DisplayVersion") as string;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public string InstalledExePath
    {
        get
        {
            var location = ReadInstallLocation();
            var dir = string.IsNullOrWhiteSpace(location) ? DefaultInstallDirectory : location;
            return Path.Combine(dir, ExeName);
        }
    }

    /// <inheritdoc />
    public bool IsInstalled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
                if (key is null)
                {
                    return false;
                }

                var location = key.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
                {
                    return false;
                }

                // 目录在不算数：必须确认安装后的 exe 真的在（否则快捷方式必然点不开）。
                // 历史故障：目录被清空但注册表残留 → 判定为已安装 → 快捷方式指向不存在的 exe。
                var exe = Path.Combine(location, ExeName);
                return File.Exists(exe) && new FileInfo(exe).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void Install(InstallOptions options, IProgress<string>? progress = null)
    {
        var target = NormalizeDirectory(string.IsNullOrWhiteSpace(options.InstallDirectory)
            ? DefaultInstallDirectory
            : options.InstallDirectory.Trim());

        var source = CurrentProcessDirectory;

        if (PathsEqual(source, target))
        {
            throw new InvalidOperationException("安装目录不能与当前运行目录相同。");
        }

        if (IsSubPath(source, target) || IsSubPath(target, source))
        {
            throw new InvalidOperationException("安装目录不能嵌套在当前运行目录之内（或反之）。");
        }

        progress?.Report("正在创建安装目录...");
        Directory.CreateDirectory(target);

        progress?.Report("正在复制程序文件...");
        AppendLog($"安装开始：{source} → {target}（{AppVersion.Display}）");
        CopyProgramFiles(source, target, progress);

        var exePath = Path.Combine(target, ExeName);

        // 安装后校验：目标 exe 必须真的在，否则快捷方式必然点不开。
        // 历史故障：目录被清空或复制被中断，但界面仍报「安装完成」。
        if (!File.Exists(exePath) || new FileInfo(exePath).Length == 0)
        {
            AppendLog($"安装校验失败：{exePath} 不存在或为空。");
            throw new InvalidOperationException(
                $"复制后未在安装目录找到可执行文件：{exePath}\n请检查是否有安全软件拦截了文件写入。");
        }

        var expected = new FileInfo(Path.Combine(source, ExeName)).Length;
        var actual = new FileInfo(exePath).Length;
        if (expected != actual)
        {
            AppendLog($"安装校验失败：exe 大小不一致（源 {expected} / 目标 {actual}）。");
            throw new InvalidOperationException(
                $"安装文件不完整（源 {expected} 字节 / 目标 {actual} 字节）。请重试或检查磁盘空间与安全软件。");
        }

        AppendLog($"文件复制完成并校验通过（{actual} 字节）。");

        // 图标使用 exe 自身（ApplicationIcon 已嵌入），避免依赖 Assets 目录被复制
        var iconPath = exePath;

        if (options.CreateDesktopShortcut)
        {
            progress?.Report("正在创建桌面快捷方式...");
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ShortcutName);
            _shortcut.CreateLink(desktop, exePath, target, iconPath, AppDisplayName);
        }

        if (options.CreateStartMenuShortcut)
        {
            progress?.Report("正在创建开始菜单快捷方式...");
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                ShortcutName);
            _shortcut.CreateLink(programs, exePath, target, iconPath, AppDisplayName);
        }

        progress?.Report("正在写入卸载信息...");
        WriteUninstallRegistry(target, exePath);

        AppendLog($"安装完成（{AppVersion.Display}）：{target}");
        progress?.Report("安装完成。");
    }

    /// <inheritdoc />
    public void LaunchInstalledCopy(string installDirectory)
    {
        var exe = Path.Combine(installDirectory, ExeName);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("未找到已安装的程序文件。", exe);
        }

        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = installDirectory,
            UseShellExecute = true,
        });
    }

    /// <inheritdoc />
    public void Uninstall(bool removeUserSettings)
    {
        // 1) 快捷方式
        _shortcut.DeleteLink(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName));
        _shortcut.DeleteLink(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName));

        var installDir = ReadInstallLocation();

        // 2) 卸载注册表项
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
        }
        catch
        {
            // 忽略：注册表项可能已不存在
        }

        // 3) 可选删除用户设置
        if (removeUserSettings)
        {
            var settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassManager");
            try
            {
                if (Directory.Exists(settingsDir))
                {
                    Directory.Delete(settingsDir, true);
                }
            }
            catch
            {
                // 忽略
            }
        }

        // 4) 调度延迟自删除：本进程退出后删除安装目录。
        //    即使当前进程正运行于安装目录之内也可安全删除（脚本会先等待本 PID 退出）。
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            ScheduleSelfDelete(installDir);
        }
    }

    /// <inheritdoc />
    public bool LaunchUninstaller(bool removeUserSettings = false)
    {
        try
        {
            // 目标安装目录：优先注册表 InstallLocation，回退当前程序所在目录
            var installDir = ReadInstallLocation();
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                installDir = CurrentProcessDirectory;
            }

            var source = CurrentProcessDirectory;

            // 临时目录名保持 ASCII（即使 TEMP 路径含中文，复制/启动也不依赖脚本编码）
            var tempDir = Path.Combine(
                Path.GetTempPath(),
                "cm-uninstaller-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            // 兼容两种发布形态：单文件版（仅 1 个 exe）与文件夹版（数百个文件）
            CopyProgramFiles(source, tempDir);

            var exe = Path.Combine(tempDir, ExeName);
            if (!File.Exists(exe))
            {
                TryDeleteDirectory(tempDir);
                return false;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("--purge");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(installDir);
            psi.ArgumentList.Add(removeUserSettings ? "true" : "false");

            Process.Start(psi);
            return true;
        }
        catch
        {
            // 复制/启动失败 → 调用方回退到内置 ScheduleSelfDelete 路径
            return false;
        }
    }

    /// <inheritdoc />
    public InstallStatus GetStatus()
    {
        var location = ReadInstallLocation();

        if (string.IsNullOrWhiteSpace(location))
        {
            // 注册表没记录 ≠ 没安装。
            // 默认安装目录里可能还留着上次安装的东西（安装中断、卸载没清干净、手动删过注册表）。
            // 不看目录就判 NotInstalled，会让「修复安装」直接放弃 ——
            // 结果是主程序、快捷方式、开始菜单全都缺着。
            var fallback = DefaultInstallDirectory;

            if (!Directory.Exists(fallback))
            {
                AppendLog("完整性检查: 未安装（无注册表记录）");
                return new InstallStatus
                {
                    State = InstallState.NotInstalled,
                    Detail = "无安装注册表记录",
                };
            }

            AppendLog($"完整性检查: 无注册表记录，但默认安装目录存在，按它检查：{fallback}");
            location = fallback;
        }

        var exePath = Path.Combine(location, ExeName);
        var existed = File.Exists(exePath);
        var length = existed ? new FileInfo(exePath).Length : 0;
        var version = InstalledVersion;

        InstallState state;
        string detail;

        if (!Directory.Exists(location))
        {
            state = InstallState.Broken;
            detail = $"安装目录不存在: {location}";
        }
        else if (!existed || length == 0)
        {
            state = InstallState.Broken;
            detail = $"主程序缺失: {exePath}";
        }
        else if (!string.Equals(version, AppVersion.Current, StringComparison.OrdinalIgnoreCase))
        {
            state = InstallState.Outdated;
            detail = $"版本不一致: 已装 {version ?? "?"} / 当前 {AppVersion.Current}";
        }
        else
        {
            state = InstallState.Healthy;
            detail = $"完整（{length:N0} 字节，v{version}）";
        }

        AppendLog($"完整性检查: {state} — {detail}");

        return new InstallStatus
        {
            State = state,
            InstallDir = location,
            ExePath = exePath,
            InstalledVersion = version,
            ExeLength = length,
            Detail = detail,
        };
    }

    /// <inheritdoc />
    public bool RepairInstall(out string message)
    {
        message = string.Empty;

        try
        {
            var status = GetStatus();

            if (status.State == InstallState.NotInstalled)
            {
                message = "未安装，无需修复。";
                return false;
            }

            var installDir = status.InstallDir;
            var currentDir = CurrentProcessDirectory;

            if (PathsEqual(currentDir, installDir))
            {
                message = "当前已在安装目录中运行，无法自我覆盖。";
                return false;
            }

            AppendLog($"开始修复安装（{status.State}）：{currentDir} → {installDir}");

            Directory.CreateDirectory(installDir);
            CopyProgramFiles(currentDir, installDir);

            var exePath = Path.Combine(installDir, ExeName);
            var expected = new FileInfo(Path.Combine(currentDir, ExeName)).Length;

            if (!File.Exists(exePath))
            {
                message = $"修复失败：安装目录缺少 {ExeName}";
                AppendLog(message);
                return false;
            }

            var actual = new FileInfo(exePath).Length;
            if (actual != expected)
            {
                message = $"修复失败：主程序大小不一致（源 {expected} / 目标 {actual}）";
                AppendLog(message);
                return false;
            }

            RefreshShortcutIfExists(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName),
                exePath,
                installDir);

            RefreshShortcutIfExists(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName),
                exePath,
                installDir);

            WriteUninstallRegistry(installDir, exePath);

            message = $"已修复安装到 {AppVersion.Display}（{actual:N0} 字节）。";
            AppendLog(message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"修复失败：{ex.Message}";
            AppendLog(message);
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryOverwriteUpgrade(out string message)
    {
        message = string.Empty;

        try
        {
            var status = GetStatus();

            if (status.State == InstallState.NotInstalled)
            {
                message = "未找到已安装位置。";
                return false;
            }

            if (status.State == InstallState.Healthy)
            {
                message = $"已是最新版本 {AppVersion.Display}。";
                return false;
            }

            return RepairInstall(out message);
        }
        catch (Exception ex)
        {
            message = $"覆盖安装失败：{ex.Message}";
            AppendLog(message);
            return false;
        }
    }

    /// <summary>仅当快捷方式已存在时刷新它（不新建，避免改动用户当初的勾选）。</summary>
    private void RefreshShortcutIfExists(string shortcutPath, string exePath, string workingDirectory)
    {
        if (!File.Exists(shortcutPath))
        {
            return;
        }

        try
        {
            _shortcut.CreateLink(shortcutPath, exePath, workingDirectory, exePath, AppDisplayName);
        }
        catch (Exception ex)
        {
            AppendLog($"刷新快捷方式失败（{shortcutPath}）：{ex.Message}");
        }
    }

    /// <summary>追加安装日志（诊断用，失败不抛出）。</summary>
    private void AppendLog(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(InstallLogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(
                InstallLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [v{AppVersion.Current}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private void WriteUninstallRegistry(string installDir, string exePath)
    {
        // 一律以规范化后的绝对路径写入，避免正/反斜杠混用污染注册表与快捷方式
        installDir = NormalizeDirectory(installDir);
        exePath = Path.Combine(installDir, ExeName);

        var author = _authorProvider();
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, true)
            ?? throw new InvalidOperationException("无法写入卸载注册表项。");

        key.SetValue("DisplayName", AppDisplayName);
        // 版本号来自程序集（csproj <Version>），不再用作者信息里的版本
        key.SetValue("DisplayVersion", AppVersion.Current);
        key.SetValue("Publisher", string.IsNullOrWhiteSpace(author.Name) ? "K." : author.Name);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        try
        {
            key.SetValue("EstimatedSize", EstimateSizeKb(installDir), RegistryValueKind.DWord);
        }
        catch
        {
            // 忽略：大小估算失败不影响安装
        }
    }

    private static string? ReadInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            var value = key?.GetValue("InstallLocation") as string;

            // 历史数据可能带正斜杠（例如先前的安装把 "C:/Users/..." 写了进去），
            // 这里统一规范化，避免后续用它重建出混合斜杠的快捷方式目标。
            return string.IsNullOrWhiteSpace(value) ? value : NormalizeDirectory(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 挑出属于「本程序」的文件 —— 安装时**只**复制这些。
    /// <para>
    /// 历史故障（用户实测截图）：安装向导把来源目录里的**所有**文件都复制过去。
    /// 用户把 exe 放在 Downloads 里 → 整个下载夹（几百个文件、浏览器正在下载的
    /// <c>.crdownload</c>、Office 临时文件、杀软正在扫描的文件）全被复制，
    /// 碰到被占用的文件就卡死在「正在复制程序文件…」。
    /// </para>
    /// <para>
    /// 单文件自包含发布时 exe 就是全部 → 只复制它自己；
    /// 文件夹发布时还需要主 dll、运行时配置与资源目录。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> CollectProgramFiles(string source)
    {
        var files = new List<string>();

        if (!Directory.Exists(source))
        {
            return files;
        }

        // 1) 主程序 exe —— 必须有
        var exe = Path.Combine(source, ExeName);

        if (File.Exists(exe))
        {
            files.Add(exe);
        }

        var stem = Path.GetFileNameWithoutExtension(ExeName);

        // 2) 文件夹发布时的托管程序集与运行时配置（单文件发布时这些不存在）
        foreach (var candidate in new[] { stem + ".dll", stem + ".deps.json", stem + ".runtimeconfig.json" })
        {
            var path = Path.Combine(source, candidate);

            if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        // 3) 资源目录（单文件发布时资源已嵌进 bundle，这里不存在）
        foreach (var dirName in new[] { "Assets", "Resources" })
        {
            var dir = Path.Combine(source, dirName);

            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                files.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            }
            catch
            {
                // 目录读不到就跳过，绝不能因此卡住安装
            }
        }

        return files;
    }

    /// <summary>
    /// 把「本程序的文件」复制到目标目录。
    /// <para>
    /// 只复制 <see cref="CollectProgramFiles"/> 挑出来的文件 —— 绝不碰用户目录里的其他东西。
    /// 单个文件被占用（浏览器正在下载 / Office 临时文件 / 杀软扫描）时跳过并记账，
    /// 不再无限卡死；主程序复制失败则直接报错。
    /// </para>
    /// </summary>
    private int CopyProgramFiles(string source, string target, IProgress<string>? progress = null)
    {
        var files = CollectProgramFiles(source);

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"在 {source} 里找不到 {ExeName}，无法安装。");
        }

        var copied = 0;
        var skipped = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            var destinationDir = Path.GetDirectoryName(destination);

            progress?.Report($"正在复制程序文件…({i + 1}/{files.Count})");

            try
            {
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                File.Copy(file, destination, true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var isMainProgram = string.Equals(file, Path.Combine(source, ExeName), StringComparison.OrdinalIgnoreCase);

                if (isMainProgram)
                {
                    throw new InvalidOperationException(
                        $"复制主程序失败：{file}\n{ex.Message}\n请关闭正在占用该文件的程序后重试。",
                        ex);
                }

                skipped++;
                AppendLog($"安装：跳过被占用的文件 {relative}（{ex.GetType().Name}）");
            }
        }

        AppendLog($"安装：复制 {copied} 个文件，跳过 {skipped} 个被占用的文件。");

        return copied;
    }

    /// <summary>
    /// 调度延迟自删除：本进程退出后删除安装目录。
    /// 采用 PowerShell「-EncodedCommand」方案——脚本命令以 UTF-16LE 编码再 Base64 传入，
    /// 命令行本身是纯 ASCII，因此中文 / 空格 / 引号 / 特殊字符路径都能被原样编码进脚本内部，
    /// 彻底规避“脚本文件用 ASCII 写导致非 ASCII 路径变 '?'”的问题；
    /// 同时命令为内联传入、不落地脚本文件，故不存在需要“删除脚本自身”的问题。
    /// </summary>
    private static void ScheduleSelfDelete(string installDir)
    {
        var targetPid = Environment.ProcessId;

        // PowerShell 单引号字符串中，仅需把 ' 转义为 ''；$、` 等均为字面量，中文与空格原样保留。
        var escapedDir = installDir.Replace("'", "''");

        var script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"$targetPid={targetPid}; " +
            "while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }; " +
            $"$dir='{escapedDir}'; " +
            // 按「时间预算」而不是固定次数重试：单文件自包含 exe（150MB 级）退出后
            // 文件解锁 + 杀软扫描可能远超十几秒，固定 60 次（约 18s）会中途放弃导致卸载残留。
            "$deadline=(Get-Date).AddSeconds(180); " +
            "while ((Test-Path -LiteralPath $dir) -and ((Get-Date) -lt $deadline)) { " +
            "Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue; " +
            "if (Test-Path -LiteralPath $dir) { Start-Sleep -Milliseconds 400 } }";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments =
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    /// <summary>
    /// 把目录规范化为「绝对路径 + 全反斜杠」。
    /// 必须做这一步：只要 InstallLocation / 快捷方式里混进正斜杠，
    /// WScript 写快捷方式时会把目标存成 <c>%SystemDrive%/Users/...\x.exe</c> 这种混合形式，快捷方式就不可靠了。
    /// </summary>
    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return path;
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

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // 尽力而为，失败不影响主流程
        }
    }

    private static bool IsSubPath(string parent, string child)
    {
        try
        {
            var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int EstimateSizeKb(string directory)
    {
        try
        {
            long total = Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            return (int)Math.Max(1, total / 1024);
        }
        catch
        {
            return 0;
        }
    }
}
