using System;
using System.IO;
using System.Threading.Tasks;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 「检查更新」全链路：发布更新源 → 读取清单 → 发现新版 → 下载安装包。
/// <para>
/// 「检查更新」功能本身早就实现了，但此前没有东西生成 version.json，
/// 缺了这一环整条链子是断的。这些测试把这条链子钉死。
/// </para>
/// </summary>
public class UpdatePublisherTests : IDisposable
{
    private readonly string _root;

    public UpdatePublisherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cm_upd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Publish_creates_manifest_and_package()
    {
        var package = Path.Combine(_root, "ClassManager.App.exe");
        File.WriteAllText(package, "fake package");

        var source = Path.Combine(_root, "source");
        var message = UpdatePublisher.Publish(package, source, "修复若干问题", "99.0");

        Assert.StartsWith("已发布", message, StringComparison.Ordinal);

        // 安装包已复制过去
        var copied = Path.Combine(source, "ClassManager.App.exe");
        Assert.True(File.Exists(copied));

        // 清单已生成
        var manifest = Path.Combine(source, "version.json");
        Assert.True(File.Exists(manifest));

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal("99.0", json["version"]!.GetValue<string>());
        Assert.Equal("ClassManager.App.exe", json["url"]!.GetValue<string>());
        Assert.Equal("修复若干问题", json["notes"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(json["sha256"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Published_source_is_detected_and_downloadable()
    {
        var package = Path.Combine(_root, "ClassManager.App.exe");
        File.WriteAllText(package, "fake package content");

        var source = Path.Combine(_root, "source");
        UpdatePublisher.Publish(package, source, "测试更新", "99.0");

        var service = new UpdateService(_root, Path.Combine(_root, "downloads"));
        service.Settings.SourceUrl = Path.Combine(source, "version.json");
        service.Settings.IgnoredVersion = string.Empty;

        var check = await service.CheckAsync();

        Assert.True(check.Ok, check.Message);
        Assert.True(check.HasUpdate, "应发现新版本");
        Assert.Equal("99.0", check.Info!.Version);

        // 相对 url 按同目录解析 → 下载应成功
        var download = await service.DownloadAsync(check.Info);

        Assert.True(download.Ok, download.Message);
        Assert.True(File.Exists(download.FilePath));
        Assert.Equal("fake package content", await File.ReadAllTextAsync(download.FilePath));
    }

    [Fact]
    public async Task Older_or_same_version_is_reported_as_latest()
    {
        var package = Path.Combine(_root, "ClassManager.App.exe");
        File.WriteAllText(package, "fake package");

        var source = Path.Combine(_root, "source");

        // 造一个比当前版本低的清单
        UpdatePublisher.Publish(package, source, "旧版本", "0.0.1");

        var service = new UpdateService(_root, Path.Combine(_root, "downloads"));
        service.Settings.SourceUrl = Path.Combine(source, "version.json");
        service.Settings.IgnoredVersion = string.Empty;

        var check = await service.CheckAsync();

        Assert.True(check.Ok, check.Message);
        Assert.False(check.HasUpdate, "旧版本不应提示有更新");
    }

    [Fact]
    public async Task Ignored_version_is_not_offered()
    {
        var package = Path.Combine(_root, "ClassManager.App.exe");
        File.WriteAllText(package, "fake package");

        var source = Path.Combine(_root, "source");
        UpdatePublisher.Publish(package, source, "测试", "99.0");

        var service = new UpdateService(_root, Path.Combine(_root, "downloads"));
        service.Settings.SourceUrl = Path.Combine(source, "version.json");
        service.Settings.IgnoredVersion = "99.0";

        var check = await service.CheckAsync();

        Assert.True(check.Ok, check.Message);
        Assert.False(check.HasUpdate, "被忽略的版本不应再提示");
    }

    [Fact]
    public void Page_url_is_written_and_read_back()
    {
        var package = Path.Combine(_root, "ClassManager.App.exe");
        File.WriteAllText(package, "fake package");

        var source = Path.Combine(_root, "source");
        UpdatePublisher.Publish(package, source, "说明", "99.0", "https://pan.quark.cn/s/xxxx");

        var json = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(source, "version.json")))!;

        Assert.Equal("https://pan.quark.cn/s/xxxx", json["page"]!.GetValue<string>());

        // 清单里也能被解析出来
        var manifest = UpdateService.ParseManifest(json.ToJsonString());
        Assert.NotNull(manifest);
        Assert.Equal("https://pan.quark.cn/s/xxxx", manifest!.PageUrl);
    }

    [Fact]
    public void Web_page_resolves_to_browser_target()
    {
        var service = new UpdateService(_root, Path.Combine(_root, "downloads"));

        var target = service.ResolveDownloadTarget(new UpdateManifest
        {
            Version = "99.0",
            Url = "https://example.com/ClassManager.App.exe",
            PageUrl = "https://pan.quark.cn/s/xxxx",
        });

        Assert.True(target.IsWeb);
        Assert.Equal("https://pan.quark.cn/s/xxxx", target.Path);
    }

    [Fact]
    public void Local_source_resolves_to_folder_target()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);

        File.WriteAllText(Path.Combine(source, "version.json"), "{}");

        var service = new UpdateService(_root, Path.Combine(_root, "downloads"));
        service.Settings.SourceUrl = Path.Combine(source, "version.json");

        var target = service.ResolveDownloadTarget(new UpdateManifest
        {
            Version = "99.0",
            Url = "ClassManager.App.exe",
        });

        // 本地更新源 → 跳资源管理器，打开安装包所在文件夹
        Assert.False(target.IsWeb);
        Assert.Equal(Path.GetFullPath(source), Path.GetFullPath(target.Path));
    }

    [Fact]
    public void Github_release_api_manifest_is_parsed()
    {
        // GitHub 的 releases/latest 接口：下载地址在 assets[].browser_download_url，不在顶层
        const string api = """
        {
          "tag_name": "v0.30",
          "name": "v0.30",
          "body": "修了若干问题",
          "assets": [
            {
              "name": "ClassManager.App.exe",
              "browser_download_url": "https://github.com/KEY-MAKE/ClassManager/releases/download/v0.30/ClassManager.App.exe",
              "size": 82742831
            }
          ]
        }
        """;

        var manifest = UpdateService.ParseManifest(api);

        Assert.NotNull(manifest);
        Assert.Equal("0.30", manifest!.Version);
        Assert.Equal("https://github.com/KEY-MAKE/ClassManager/releases/download/v0.30/ClassManager.App.exe", manifest.Url);
        Assert.Equal(82742831, manifest.SizeBytes);
        Assert.Equal("修了若干问题", manifest.Notes);
    }

    [Fact]
    public void Mirrors_are_parsed()
    {
        const string json = """
        {
          "version": "0.30",
          "url": "https://github.com/KEY-MAKE/ClassManager/releases/download/v0.30/ClassManager.App.exe",
          "mirrors": [
            "https://cdn.example.com/ClassManager.App.exe",
            "https://backup.example.com/ClassManager.App.exe"
          ]
        }
        """;

        var manifest = UpdateService.ParseManifest(json);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Mirrors.Count);
        Assert.Contains("cdn.example.com", manifest.Mirrors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Github_mode_writes_release_direct_link_and_page()
    {
        var package = Path.Combine(_root, "ClassManager.App.exe");
        File.WriteAllText(package, "fake package");

        var source = Path.Combine(_root, "source");
        UpdatePublisher.Publish(package, source, "说明", "0.30", githubRepo: "KEY-MAKE/ClassManager");

        var json = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(source, "version.json")))!;

        Assert.Equal(
            "https://github.com/KEY-MAKE/ClassManager/releases/download/v0.30/ClassManager.App.exe",
            json["url"]!.GetValue<string>());

        Assert.Equal(
            "https://github.com/KEY-MAKE/ClassManager/releases/latest",
            json["page"]!.GetValue<string>());
    }

    [Fact]
    public void Default_source_path_is_inside_user_profile()
    {
        // 默认更新源必须落在用户目录里，不能写到系统目录
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            UpdatePublisher.DefaultSourceDirectory,
            StringComparison.OrdinalIgnoreCase);
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
