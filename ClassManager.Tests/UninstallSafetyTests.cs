using System;
using System.IO;
using System.Linq;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 卸载安全闸的契约。
/// <para>
/// <b>真实事故</b>：用户在 <c>C:\Users\xxx\Downloads</c> 里双击 exe（注册表无记录），
/// 旧代码把「当前程序所在目录」当成安装目录，<c>--purge</c> 递归删光了整个下载夹。
/// <see cref="Directory.Delete(string, bool)"/> 不进回收站，数据永久丢失。
/// </para>
/// <para>
/// 所以这里的每一个用例都是在防"删错目录"，任何一个失败都意味着会再次删掉用户数据。
/// </para>
/// </summary>
public class UninstallSafetyTests : IDisposable
{
    private readonly string _root;

    public UninstallSafetyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm_safe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private static bool IsSafe(string? dir) =>
        InstallService.IsSafeUninstallTarget(dir, out _);

    /// <summary>下载目录绝不能被当成安装目录。</summary>
    [Fact]
    public void Downloads_is_rejected()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        Assert.False(IsSafe(downloads), "Downloads 绝不能被卸载流程删除");
    }

    /// <summary>即使下载目录里正好有我们的 exe（用户刚下载的），也必须拒绝。</summary>
    [Fact]
    public void Downloads_with_our_exe_inside_is_still_rejected()
    {
        var fakeDownloads = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(fakeDownloads);

        // 用户下载的 exe 就在这儿
        File.WriteAllText(Path.Combine(fakeDownloads, "ClassManager.App.exe"), "exe");

        // 还有几百个别的文件
        for (var i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(fakeDownloads, $"file{i}.bin"), "x");
        }

        Assert.False(IsSafe(fakeDownloads), "塞满文件的目录不可能是安装目录");
    }

    /// <summary>桌面、文档、用户主目录——全部拒绝。</summary>
    [Theory]
    [InlineData(Environment.SpecialFolder.DesktopDirectory)]
    [InlineData(Environment.SpecialFolder.MyDocuments)]
    [InlineData(Environment.SpecialFolder.MyPictures)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    public void User_folders_are_rejected(Environment.SpecialFolder folder)
    {
        Assert.False(IsSafe(Environment.GetFolderPath(folder)));
    }

    /// <summary>盘根拒绝。</summary>
    [Fact]
    public void Drive_root_is_rejected()
    {
        var root = Path.GetPathRoot(_root);

        Assert.NotNull(root);
        Assert.False(IsSafe(root));
    }

    /// <summary>空路径 / null 拒绝。</summary>
    [Fact]
    public void Null_and_empty_are_rejected()
    {
        Assert.False(InstallService.IsSafeUninstallTarget(null, out _));
        Assert.False(InstallService.IsSafeUninstallTarget(string.Empty, out _));
        Assert.False(InstallService.IsSafeUninstallTarget("   ", out _));
    }

    /// <summary>不存在的目录——不删（本来也没什么可删）。</summary>
    [Fact]
    public void Nonexistent_directory_is_rejected()
    {
        Assert.False(IsSafe(Path.Combine(_root, "nope-not-here")));
    }

    /// <summary>正常安装目录（只有几个程序文件）→ 允许删除。</summary>
    [Fact]
    public void Real_install_directory_is_allowed()
    {
        var install = Path.Combine(_root, "Programs", "ClassManager");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "ClassManager.App.exe"), "exe");

        // 必须先有安装标记 —— 这是「这个目录确实是我们装的」的唯一证据
        File.WriteAllText(Path.Combine(install, ".classmanager-install"), "installedAt=2026-10-31");

        Assert.True(IsSafe(install), "正常的安装目录必须能卸载");

        Assert.True(InstallService.IsSafeUninstallTarget(install, out var reason), reason);
    }

    /// <summary>文件夹发布形态（exe + dll + 资源）也允许。</summary>
    [Fact]
    public void Folder_deployment_install_is_allowed()
    {
        var install = Path.Combine(_root, "App");
        Directory.CreateDirectory(Path.Combine(install, "Assets"));
        File.WriteAllText(Path.Combine(install, "ClassManager.App.exe"), "exe");
        File.WriteAllText(Path.Combine(install, "ClassManager.App.dll"), "dll");
        File.WriteAllText(Path.Combine(install, "Assets", "icon.ico"), "ico");
        File.WriteAllText(Path.Combine(install, ".classmanager-install"), "installedAt=2026-10-31");

        Assert.True(IsSafe(install));
    }

    /// <summary>只有目录名像，没有安装标记 → 必须拒绝。</summary>
    [Fact]
    public void Directory_named_classmanager_without_marker_is_rejected()
    {
        var install = Path.Combine(_root, "ClassManager");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "leftover.log"), "x");

        Assert.False(IsSafe(install), "光名字对不算，必须有安装标记");
    }

    /// <summary>有标记但主程序没了（半装状态）→ 仍然拒绝（不猜）。</summary>
    [Fact]
    public void Marker_without_executable_is_rejected()
    {
        var install = Path.Combine(_root, "ClassManager");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, ".classmanager-install"), "x");

        Assert.False(IsSafe(install));
    }

    /// <summary>这次事故的完整复刻：Downloads 里有 exe、也有标记，但内容太多 → 依然拒绝。</summary>
    [Fact]
    public void Downloads_with_marker_and_exe_is_still_rejected()
    {
        var fake = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake, "ClassManager.App.exe"), "exe");
        File.WriteAllText(Path.Combine(fake, ".classmanager-install"), "x");

        for (var i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(fake, $"f{i}.bin"), "x");
        }

        Assert.False(IsSafe(fake));
    }

    /// <summary>名字不相关、也没本程序 → 拒绝（不能拿它当安装目录删）。</summary>
    [Fact]
    public void Unrelated_directory_is_rejected()
    {
        var other = Path.Combine(_root, "SomeOtherApp");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "other.exe"), "x");

        Assert.False(IsSafe(other));
    }

    /// <summary>拒绝时要给出可读的原因（会写进日志，便于排查）。</summary>
    [Fact]
    public void Rejection_provides_a_reason()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        Assert.False(InstallService.IsSafeUninstallTarget(downloads, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 忽略
        }
    }
}
