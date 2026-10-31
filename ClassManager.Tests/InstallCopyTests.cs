using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 安装复制逻辑的契约。
/// <para>
/// 历史故障（用户实测截图）：安装向导卡死在「正在复制程序文件…」。
/// 根因是它把来源目录里的**所有**文件都复制过去 —— 用户把 exe 放在 Downloads 里，
/// 于是整个下载夹（几百个文件、浏览器锁住的 .crdownload、Office 临时文件、
/// 杀软正在扫描的文件）全被复制，碰到被占用的文件就卡死。
/// </para>
/// </summary>
public class InstallCopyTests : IDisposable
{
    private readonly string _root;

    public InstallCopyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm_install_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private static IReadOnlyList<string> Collect(string source)
    {
        var method = typeof(InstallService).GetMethod(
            "CollectProgramFiles",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { source }) as IReadOnlyList<string>;

        Assert.NotNull(result);

        return result!;
    }

    /// <summary>安装时只应复制本程序的文件，绝不碰用户目录里的其他东西。</summary>
    [Fact]
    public void Only_program_files_are_collected()
    {
        var source = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(source);

        File.WriteAllText(Path.Combine(source, "ClassManager.App.exe"), "fake exe");

        // 用户下载夹里的其它东西
        File.WriteAllText(Path.Combine(source, "工作表.xlsx"), "user file");
        File.WriteAllText(Path.Combine(source, "movie.mp4"), "user movie");
        File.WriteAllText(Path.Combine(source, "部分下载.crdownload"), "in-progress download");

        Directory.CreateDirectory(Path.Combine(source, "subfolder"));
        File.WriteAllText(Path.Combine(source, "subfolder", "note.txt"), "user note");

        var files = Collect(source);

        Assert.Single(files);
        Assert.EndsWith("ClassManager.App.exe", files[0], StringComparison.Ordinal);
    }

    /// <summary>文件夹发布形态：exe + 主 dll + 运行时配置 + 资源目录都要带上。</summary>
    [Fact]
    public void Folder_deployment_files_are_collected()
    {
        var source = Path.Combine(_root, "FolderPublish");
        Directory.CreateDirectory(source);

        File.WriteAllText(Path.Combine(source, "ClassManager.App.exe"), "fake exe");
        File.WriteAllText(Path.Combine(source, "ClassManager.App.dll"), "fake dll");
        File.WriteAllText(Path.Combine(source, "ClassManager.App.deps.json"), "{}");
        File.WriteAllText(Path.Combine(source, "ClassManager.App.runtimeconfig.json"), "{}");

        Directory.CreateDirectory(Path.Combine(source, "Assets", "engine"));
        File.WriteAllText(Path.Combine(source, "Assets", "engine", "payload.zip"), "zip");

        // 不该被带上的
        File.WriteAllText(Path.Combine(source, "readme.txt"), "user file");

        var files = Collect(source);
        var names = files.Select(Path.GetFileName).ToList();

        Assert.Contains("ClassManager.App.exe", names);
        Assert.Contains("ClassManager.App.dll", names);
        Assert.Contains("ClassManager.App.deps.json", names);
        Assert.Contains("ClassManager.App.runtimeconfig.json", names);
        Assert.Contains("payload.zip", names);
        Assert.DoesNotContain("readme.txt", names);
    }

    /// <summary>来源目录里没有主程序 → 明确报错，而不是复制一堆无关文件。</summary>
    [Fact]
    public void Missing_main_program_is_reported()
    {
        var source = Path.Combine(_root, "EmptyDir");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "unrelated.txt"), "x");

        Assert.Empty(Collect(source));
    }

    /// <summary>复制时用户目录里的其它文件不会被带进安装目录。</summary>
    [Fact]
    public void Copy_does_not_touch_unrelated_files()
    {
        var source = Path.Combine(_root, "Src2");
        var target = Path.Combine(_root, "Dst2");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        File.WriteAllText(Path.Combine(source, "ClassManager.App.exe"), "fake exe");
        File.WriteAllText(Path.Combine(source, "用户文件.txt"), "user file");

        var method = typeof(InstallService).GetMethod(
            "CopyProgramFiles",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var service = new InstallService(new FakeShortcutService(), () => AuthorInfo.Default());

        method!.Invoke(service, new object?[] { source, target, null });

        Assert.True(File.Exists(Path.Combine(target, "ClassManager.App.exe")));
        Assert.False(File.Exists(Path.Combine(target, "用户文件.txt")), "用户的文件不该被复制进安装目录");
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

    private sealed class FakeShortcutService : IShortcutService
    {
        public void CreateLink(
            string shortcutPath,
            string targetExe,
            string workingDirectory,
            string iconPath,
            string description)
        {
        }

        public void DeleteLink(string shortcutPath) { }
    }
}
