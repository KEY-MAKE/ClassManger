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

    /// <summary>
    /// 安装标记文件名。安装成功时写在安装目录里，内容是安装时间 + 版本 + 随机 token。
    /// <para>
    /// 卸载时**必须**找到它才允许删目录 —— 这是"这个目录确实是我们装的"的唯一证据。
    /// 光看目录名或有没有 exe 都不够：用户下载的 exe 也在 Downloads 里，
    /// 只看文件名就会把下载夹当成安装目录（真实事故，已造成数据丢失）。
    /// </para>
    /// </summary>
    private const string InstallMarkerFileName = ".classmanager-install";

    /// <summary>在安装目录写入标记文件。</summary>
    private static void WriteInstallMarker(string installDir)
    {
        try
        {
            var content = string.Join(
                Environment.NewLine,
                $"installedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"version={AppVersion.Current}",
                $"token={Guid.NewGuid():N}");

            File.WriteAllText(Path.Combine(installDir, InstallMarkerFileName), content);
        }
        catch
        {
            // 写不了标记不算安装失败，但该目录之后将无法通过"卸载"删除（安全第一）
        }
    }

    /// <summary>该目录里有没有我们写的安装标记。</summary>
    public static bool HasInstallMarker(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(dir, InstallMarkerFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 删除前的最后一道安全闸：判断某个路径能不能当"安装目录"删掉。
    /// <para>
    /// 真实事故（用户数据丢失）：在 <c>Downloads</c> 里双击 exe → 注册表无记录 →
    /// 旧代码把「当前程序所在目录」当成安装目录 → <c>--purge</c> 递归删光了整个下载夹。
    /// </para>
    /// <para>
    /// 因此这里宁可拒绝删除（让用户手动清），也绝不允许误删用户目录。
    /// </para>
    /// </summary>
    public static bool IsSafeUninstallTarget(string? dir, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(dir))
        {
            reason = "安装目录为空";
            return false;
        }

        string full;

        try
        {
            full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            reason = "路径无法解析";
            return false;
        }

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            reason = "不能删除盘根目录";
            return false;
        }

        // 用户常见目录：绝不允许被"卸载"删除
        var protectedPaths = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetTempPath(),

            // 下载目录不对应 SpecialFolder，按用户目录拼
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };

        foreach (var candidate in protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string c;

            try
            {
                c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                continue;
            }

            if (string.Equals(full, c, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"这是系统/用户目录，不是安装目录：{c}";
                return false;
            }
        }

        if (!Directory.Exists(full))
        {
            reason = "目录不存在";
            return false;
        }

        // 唯一可信的证据：安装时写下的标记文件。
        // 只看目录名或有没有 exe 都不够 —— 用户下载的 exe 也躺在 Downloads 里，
        // 凭「有 exe」就把下载夹当安装目录删掉，是已经发生过的事故。
        if (!HasInstallMarker(full))
        {
            reason = $"目录里没有安装标记（{InstallMarkerFileName}），无法确认是本程序的安装目录";
            return false;
        }

        // 有标记的目录还必须真的装着主程序（标记 + exe 双条件）
        if (!File.Exists(Path.Combine(full, ExeName)))
        {
            reason = "目录里有安装标记但缺少主程序";
            return false;
        }

        // 最后一道：安装目录里只应该有本程序的少数文件。
        // 一个塞满几百项文件的目录（比如下载夹）绝不可能是安装目录。
        try
        {
            if (Directory.EnumerateFileSystemEntries(full).Take(21).Count() > 20)
            {
                reason = "目录内容过多，不像安装目录";
                return false;
            }
        }
        catch
        {
            // 读不了就按安全处理
            reason = "无法枚举目录内容";
            return false;
        }

        return true;
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

        // 目标里已经有同名程序（重装/升级到同一目录）→ 等它释放
        if (!WaitUntilFileUnlocked(Path.Combine(target, ExeName), TimeSpan.FromSeconds(10)))
        {
            AppendLog($"等待目标文件释放超时：{Path.Combine(target, ExeName)}");
        }

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

        var sourceExe = FindMainExecutable(source)
                        ?? throw new InvalidOperationException($"在 {source} 里找不到主程序，无法校验。");
        var expected = new FileInfo(sourceExe).Length;
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
        WriteInstallMarker(target);

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
        // 只删注册表记录的那个目录，且必须通过安全闸 —— 绝不猜路径
        if (IsSafeUninstallTarget(installDir, out var unsafeReason) && installDir is not null)
        {
            ScheduleSelfDelete(installDir);
        }
        else
        {
            AppendLog($"跳过安装目录删除：{unsafeReason}（installDir={installDir}）");
        }
    }

    /// <inheritdoc />
    public bool LaunchUninstaller(bool removeUserSettings = false)
    {
        try
        {
            // 目标安装目录**只认注册表记录**。
            // 绝对不能回退到「当前程序所在目录」：用户在 Downloads 里双击 exe 时，
            // 那就是 Downloads，后面的 --purge 会把整个下载夹递归删光（真实事故，已造成用户数据丢失）。
            var installDir = ReadInstallLocation();

            if (!IsSafeUninstallTarget(installDir, out var unsafeReason))
            {
                AppendLog($"拒绝启动卸载器：{unsafeReason}（installDir={installDir}）");
                return false;
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
            psi.ArgumentList.Add(installDir!);
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
            // 注册表没记录 ≠ 没安装，但也**不能**无条件去看默认安装目录。
            //
            // 真实事故：用户在 Downloads 里双击 exe，旧逻辑去查默认安装目录，
            // 发现那里有个装好的旧版本 → 判定「已安装」→ 不弹安装向导、直接进主界面，
            // 随后点卸载把 Downloads 当成了安装目录（数据丢失）。
            //
            // 所以只有「当前程序本身就跑在默认安装目录里」时才用目录兜底 ——
            // 那种情况是注册表丢了（卸载没清干净/手动删过），应该能自我修复。
            var fallback = DefaultInstallDirectory;
            var runningFromInstallDir = PathsEqual(CurrentProcessDirectory, fallback);

            if (!runningFromInstallDir || !Directory.Exists(fallback))
            {
                AppendLog($"完整性检查: 未安装（无注册表记录，运行目录={CurrentProcessDirectory}）");
                return new InstallStatus
                {
                    State = InstallState.NotInstalled,
                    Detail = "无安装注册表记录",
                };
            }

            AppendLog($"完整性检查: 无注册表记录，但当前就在默认安装目录里运行，按它检查：{fallback}");
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

            // 应用内更新：旧进程可能还在退出，先等目标 exe 释放，否则覆盖必失败
            var targetExeBeforeCopy = Path.Combine(installDir, ExeName);

            if (!WaitUntilFileUnlocked(targetExeBeforeCopy, TimeSpan.FromSeconds(20)))
            {
                AppendLog($"等待目标文件释放超时：{targetExeBeforeCopy}");
            }

            CopyProgramFiles(currentDir, installDir);

            var exePath = Path.Combine(installDir, ExeName);
            var sourceExePath = FindMainExecutable(currentDir);

            if (sourceExePath is null)
            {
                message = $"修复失败：在 {currentDir} 里找不到主程序";
                AppendLog(message);
                return false;
            }

            var expected = new FileInfo(sourceExePath).Length;

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
            WriteInstallMarker(installDir);

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
    /// 等目标文件被释放（最多 <paramref name="budget"/>）。
    /// <para>
    /// 为什么需要：应用内点「立即更新」的时序是
    /// <c>Launch(新exe)</c> → <c>Shutdown()</c>，两个进程是并发的。
    /// 新进程往往比旧进程退得早，此时去覆盖 <c>ClassManager.App.exe</c>
    /// 就会撞上「文件正被另一个进程使用」→ 更新失败。
    /// </para>
    /// <para>返回 true 表示可以写（或文件不存在），false 表示超时仍被占用。</para>
    /// </summary>
    private static bool WaitUntilFileUnlocked(string path, TimeSpan budget)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < budget)
        {
            try
            {
                // 能以「不允许共享」的方式打开，说明没有别的进程占着它
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
            catch
            {
                // 权限等问题：交给后面真正的复制去报错
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 在目录里找出本程序的主 exe。
    /// <para>
    /// 优先标准名 <c>ClassManager.App.exe</c>；找不到就退而求其次接受
    /// <c>ClassManager.App-0.44.exe</c> 这种带版本号的名字 ——
    /// 那是「应用内更新」下载下来的文件名。
    /// </para>
    /// <para>
    /// 这里踩过坑：下载侧把文件存成带版本号的名字，安装侧却只认标准名，
    /// 结果每次「更新完重启还是旧版」—— 覆盖安装一直报
    /// 「在 updates 里找不到 ClassManager.App.exe」。
    /// </para>
    /// </summary>
    private static string? FindMainExecutable(string dir)
    {
        try
        {
            var standard = Path.Combine(dir, ExeName);

            if (File.Exists(standard))
            {
                return standard;
            }

            if (!Directory.Exists(dir))
            {
                return null;
            }

            // 带版本号的下载产物：取最新那个
            return Directory
                .EnumerateFiles(dir, "ClassManager.App*.exe", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
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

        // 1) 主程序 exe —— 必须有（兼容带版本号的下载文件名）
        var exe = FindMainExecutable(source);

        if (exe is not null)
        {
            files.Add(exe);
        }

        var stem = Path.GetFileNameWithoutExtension(ExeName);

        // 2) 文件夹发布时的托管程序集与运行时配置（单文件发布时这些不存在）
        //
        // 只认**精确文件名**，不碰任何子目录。
        // 曾经这里还有 "Assets" / "Resources" 目录白名单，结果用户在 Downloads 里
        // 正好有同名目录（某个游戏的贴图，22 个文件），被整个复制进了安装目录 ——
        // Windows 大小写不敏感，连 `resources` 和小写版都一起中招。
        // 而本项目的 Resources 全部是 EmbeddedResource（编译进 dll/bundle），
        // 根本不需要按目录复制。
        foreach (var candidate in new[] { stem + ".dll", stem + ".deps.json", stem + ".runtimeconfig.json" })
        {
            var path = Path.Combine(source, candidate);

            if (File.Exists(path))
            {
                files.Add(path);
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

        var mainExe = FindMainExecutable(source);
        var copied = 0;
        var skipped = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var isMainProgram = mainExe is not null
                                && string.Equals(file, mainExe, StringComparison.OrdinalIgnoreCase);

            // 主程序在安装目录里必须叫标准名：更新下载的是
            // ClassManager.App-0.44.exe，不改名的话装进去的 exe 名字不对，
            // 下次启动找不到主程序（表现为「更新完还是旧版」）。
            var destination = isMainProgram
                ? Path.Combine(target, ExeName)
                : Path.Combine(target, Path.GetRelativePath(source, file));

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
                if (isMainProgram)
                {
                    throw new InvalidOperationException(
                        $"复制主程序失败：{file}\n{ex.Message}\n请关闭正在占用该文件的程序后重试。",
                        ex);
                }

                skipped++;
                AppendLog($"安装：跳过被占用的文件 {Path.GetFileName(file)}（{ex.GetType().Name}）");
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
