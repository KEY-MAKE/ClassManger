using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>安装/卸载服务接口。</summary>
public interface IInstallService
{
    /// <summary>是否已安装（HKCU 卸载项存在 且 安装目录与安装后的 exe 都存在）。</summary>
    bool IsInstalled { get; }

    /// <summary>注册表记录的已安装版本（未安装或未记录时为 null）。</summary>
    string? InstalledVersion { get; }

    /// <summary>
    /// 检查安装完整性（注册表 + 安装目录 + 主程序文件大小）。
    /// 结果同时写入安装日志，便于事后定位"文件被删"这类环境问题。
    /// </summary>
    InstallStatus GetStatus();

    /// <summary>
    /// 自愈式修复：把当前程序目录重新复制到安装目录、重建快捷方式、更新注册表。
    /// 用于「已安装但文件缺失」与「版本不一致」两种情况。
    /// </summary>
    /// <param name="message">结果说明。</param>
    /// <returns>true 表示修复成功。</returns>
    bool RepairInstall(out string message);

    /// <summary>
    /// 若已安装且版本与当前不同（升级或修复），把当前程序目录覆盖到安装目录并更新注册表版本。
    /// </summary>
    /// <param name="message">结果说明（成功或失败原因）。</param>
    /// <returns>true 表示已执行覆盖，调用方应重启到安装副本。</returns>
    bool TryOverwriteUpgrade(out string message);

    /// <summary>安装日志路径（诊断用）。</summary>
    string InstallLogPath { get; }

    /// <summary>默认安装目录（%LocalAppData%\Programs\ClassManager）。</summary>
    string DefaultInstallDirectory { get; }

    /// <summary>当前进程所在目录（向导复制源）。</summary>
    string CurrentProcessDirectory { get; }

    /// <summary>已安装的可执行文件路径。</summary>
    string InstalledExePath { get; }

    /// <summary>执行安装。</summary>
    void Install(InstallOptions options, IProgress<string>? progress = null);

    /// <summary>启动安装目录中的副本。</summary>
    void LaunchInstalledCopy(string installDirectory);

    /// <summary>卸载（删除快捷方式 / 注册表项 / 可选设置，并调度自删除）。</summary>
    void Uninstall(bool removeUserSettings);

    /// <summary>
    /// 启动独立卸载程序：把当前程序目录复制到临时目录，由那份副本等待本进程退出后完成清理。
    /// 关键点——执行删除的进程不能运行在待删目录内，否则文件被自己锁住。
    /// </summary>
    /// <param name="removeUserSettings">是否连带删除 %AppData%\ClassManager。</param>
    /// <returns>是否成功启动独立卸载程序（false 时调用方应回退 <see cref="Uninstall"/>）。</returns>
    bool LaunchUninstaller(bool removeUserSettings = false);
}
