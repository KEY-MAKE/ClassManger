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
/// 「更新下载的文件名带版本号」的兼容契约。
/// <para>
/// <b>真实故障</b>：应用内更新下载下来的是 <c>ClassManager.App-0.44.exe</c>，
/// 但覆盖安装只认 <c>ClassManager.App.exe</c> —— 于是每次都报
/// 「在 updates 里找不到 ClassManager.App.exe」，
/// 表现就是「下载完了、重启还是旧版本」。
/// </para>
/// <para>
/// 修复要求：能找到带版本号的主程序，并且装进安装目录时改成标准名。
/// </para>
/// </summary>
public class VersionedExeInstallTests : IDisposable
{
    private readonly string _root;

    public VersionedExeInstallTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm_ver_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private static string? FindMain(string dir)
    {
        var m = typeof(InstallService).GetMethod(
            "FindMainExecutable", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(m);
        return m!.Invoke(null, new object[] { dir }) as string;
    }

    private static IReadOnlyList<string> Collect(string source)
    {
        var m = typeof(InstallService).GetMethod(
            "CollectProgramFiles", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(m);
        return (IReadOnlyList<string>)m!.Invoke(null, new object[] { source })!;
    }

    private static int Copy(string source, string target)
    {
        var m = typeof(InstallService).GetMethod(
            "CopyProgramFiles", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(m);

        var service = new InstallService(new FakeShortcutService(), () => AuthorInfo.Default());
        return (int)m!.Invoke(service, new object?[] { source, target, null })!;
    }

    /// <summary>标准名优先于带版本号的文件。</summary>
    [Fact]
    public void Standard_name_wins()
    {
        var dir = Path.Combine(_root, "a");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ClassManager.App.exe"), "standard");
        File.WriteAllText(Path.Combine(dir, "ClassManager.App-0.44.exe"), "versioned");

        var found = FindMain(dir);

        Assert.NotNull(found);
        Assert.Equal("ClassManager.App.exe", Path.GetFileName(found!));
    }

    /// <summary>只有带版本号的文件时也能找到。</summary>
    [Fact]
    public void Versioned_name_is_found()
    {
        var dir = Path.Combine(_root, "b");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ClassManager.App-0.44.exe"), "versioned");

        var found = FindMain(dir);

        Assert.NotNull(found);
        Assert.Equal("ClassManager.App-0.44.exe", Path.GetFileName(found!));
    }

    /// <summary>.part 未完成的下载不能被当成有效主程序。</summary>
    [Fact]
    public void Part_file_is_ignored()
    {
        var dir = Path.Combine(_root, "c");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ClassManager.App-0.43.exe.part"), "partial");

        Assert.Null(FindMain(dir));
    }

    /// <summary>CollectProgramFiles 能把带版本号的主程序挑进来。</summary>
    [Fact]
    public void Collect_includes_versioned_exe()
    {
        var dir = Path.Combine(_root, "d");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ClassManager.App-0.44.exe"), "versioned");
        File.WriteAllText(Path.Combine(dir, "unrelated.txt"), "x");

        var files = Collect(dir);

        Assert.Single(files);
        Assert.Equal("ClassManager.App-0.44.exe", Path.GetFileName(files[0]));
    }

    /// <summary>
    /// 核心：复制到安装目录时，带版本号的主程序必须改名成标准名，
    /// 否则下次启动找不到主程序。
    /// </summary>
    [Fact]
    public void Copy_renames_versioned_exe_to_standard()
    {
        var source = Path.Combine(_root, "updates");
        var target = Path.Combine(_root, "Programs", "ClassManager");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        File.WriteAllText(Path.Combine(source, "ClassManager.App-0.44.exe"), "the-binary");

        var copied = Copy(source, target);

        Assert.Equal(1, copied);
        Assert.True(File.Exists(Path.Combine(target, "ClassManager.App.exe")),
            "安装目录里必须出现标准名的主程序");
        Assert.False(File.Exists(Path.Combine(target, "ClassManager.App-0.44.exe")),
            "不该同时留下带版本号的副本");
        Assert.Equal("the-binary", File.ReadAllText(Path.Combine(target, "ClassManager.App.exe")));
    }

    /// <summary>标准名复制时保持原名（不产生多余文件）。</summary>
    [Fact]
    public void Copy_keeps_standard_name()
    {
        var source = Path.Combine(_root, "e");
        var target = Path.Combine(_root, "out-e");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        File.WriteAllText(Path.Combine(source, "ClassManager.App.exe"), "binary");

        Copy(source, target);

        Assert.True(File.Exists(Path.Combine(target, "ClassManager.App.exe")));

        var all = Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories);
        Assert.Single(all);
    }

    /// <summary>文件夹部署形态 + 带版本号主程序：主程序改名，其他保留原名。</summary>
    [Fact]
    public void Copy_renames_main_and_keeps_others()
    {
        var source = Path.Combine(_root, "f");
        var target = Path.Combine(_root, "out-f");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        File.WriteAllText(Path.Combine(source, "ClassManager.App-0.44.exe"), "binary");
        File.WriteAllText(Path.Combine(source, "ClassManager.App.dll"), "dll");
        File.WriteAllText(Path.Combine(source, "ClassManager.App.deps.json"), "{}");

        var copied = Copy(source, target);

        Assert.Equal(3, copied);
        Assert.True(File.Exists(Path.Combine(target, "ClassManager.App.exe")));
        Assert.True(File.Exists(Path.Combine(target, "ClassManager.App.dll")));
        Assert.True(File.Exists(Path.Combine(target, "ClassManager.App.deps.json")));
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
        public void CreateLink(string a, string b, string c, string d, string e) { }

        public void DeleteLink(string p) { }
    }
}
