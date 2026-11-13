using System.IO;
using System.Threading.Tasks;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 检查更新：清单解析、版本号比较、本地（共享盘/网盘）更新源的检查与下载。
/// 全部走本地文件，不需要网络。
/// </summary>
public class UpdateServiceTests
{
    private const string StandardJson = """
    {
      "version": "99.0",
      "url": "https://example.com/ClassManager.App.exe",
      "size": 82624047,
      "sha256": "",
      "notes": "修复点名卡顿",
      "publishedAt": "2026-10-31"
    }
    """;

    [Fact]
    public void ParseManifest_标准字段()
    {
        var manifest = UpdateService.ParseManifest(StandardJson);

        Assert.NotNull(manifest);
        Assert.Equal("99.0", manifest!.Version);
        Assert.Equal("v99.0", manifest.DisplayVersion);
        Assert.Equal("https://example.com/ClassManager.App.exe", manifest.Url);
        Assert.Equal(82624047, manifest.SizeBytes);
        Assert.Equal("修复点名卡顿", manifest.Notes);
        Assert.Equal("2026-10-31", manifest.PublishedAt);
        Assert.Equal("78.8 MB", manifest.SizeText);
    }

    [Fact]
    public void ParseManifest_容忍别名与v前缀()
    {
        const string json = """
        {
          "tag_name": "v0.20",
          "download_url": "https://example.com/app.exe",
          "body": "说明",
          "size_bytes": 1024
        }
        """;

        var manifest = UpdateService.ParseManifest(json);

        Assert.NotNull(manifest);
        Assert.Equal("0.20", manifest!.Version);
        Assert.Equal("https://example.com/app.exe", manifest.Url);
        Assert.Equal("说明", manifest.Notes);
        Assert.Equal(1024, manifest.SizeBytes);
    }

    [Fact]
    public void ParseManifest_缺字段或非法json_返回null()
    {
        Assert.Null(UpdateService.ParseManifest("{\"notes\":\"没有版本号\"}"));
        Assert.Null(UpdateService.ParseManifest("{ 这不是 json"));
    }

    [Theory]
    [InlineData("0.11", "0.1", 1)]
    [InlineData("0.1", "0.11", -1)]
    [InlineData("0.18", "0.18", 0)]
    [InlineData("1.0", "0.99", 1)]
    public void CompareVersions_按版本语义(string left, string right, int expectedSign)
    {
        var actual = UpdateService.CompareVersions(left, right);
        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    /// <summary>
    /// 更新源现在固定内置：即使 settings.json 里存过别的地址（哪怕是空），
    /// 构造完也必须被覆盖成内置值 —— 用户改不了。
    /// </summary>
    [Fact]
    public void 构造后更新源被强制设为内置值()
    {
        using var temp = new TempDirectory();

        // 先写一份「用户自定义」的设置
        var store = new UpdateSettingsStore(temp.Path);
        var settings = store.Load();
        settings.SourceUrl = "https://user-custom.invalid/version.json";
        store.Save(settings);

        // 新建服务 → 必须无视上面的值
        var service = new UpdateService(temp.Path, Path.Combine(temp.Path, "updates"));

        Assert.Equal(UpdateService.BuiltinSourceUrl, service.Settings.SourceUrl);
        Assert.NotEqual("https://user-custom.invalid/version.json", service.Settings.SourceUrl);
    }

    [Fact]
    public async Task CheckAsync_本地源_发现新版本且相对url解析到同目录()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "version.json"), StandardJson);

        var service = new UpdateService(temp.Path, Path.Combine(temp.Path, "updates"));
        service.Settings.SourceUrl = Path.Combine(temp.Path, "version.json");
        service.Settings.IgnoredVersion = string.Empty;
        service.Save();

        var result = await service.CheckAsync();

        Assert.True(result.Ok);
        Assert.True(result.HasUpdate);
        Assert.Equal("99.0", result.Info!.Version);
        // url 是 http 直链，保持原样
        Assert.Equal("https://example.com/ClassManager.App.exe", result.Info.Url);
    }

    [Fact]
    public async Task CheckAsync_本地源_相对url按同目录解析()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, "version.json"),
            """{"version":"99.0","url":"ClassManager.App.exe"}""");

        var service = new UpdateService(temp.Path, Path.Combine(temp.Path, "updates"));
        service.Settings.SourceUrl = Path.Combine(temp.Path, "version.json");
        service.Save();

        var result = await service.CheckAsync();

        Assert.True(result.HasUpdate);
        Assert.Equal(Path.Combine(temp.Path, "ClassManager.App.exe"), result.Info!.Url);
    }

    [Fact]
    public async Task CheckAsync_已是最新()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, "version.json"),
            """{"version":"0.01","url":"ClassManager.App.exe"}""");

        var service = new UpdateService(temp.Path, Path.Combine(temp.Path, "updates"));
        service.Settings.SourceUrl = Path.Combine(temp.Path, "version.json");
        service.Save();

        var result = await service.CheckAsync();

        Assert.True(result.Ok);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckAsync_被忽略的版本不再提示()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, "version.json"),
            """{"version":"99.0","url":"ClassManager.App.exe"}""");

        var service = new UpdateService(temp.Path, Path.Combine(temp.Path, "updates"));
        service.Settings.SourceUrl = Path.Combine(temp.Path, "version.json");
        service.Settings.IgnoredVersion = "99.0";
        service.Save();

        var result = await service.CheckAsync();

        Assert.True(result.Ok);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task DownloadAsync_本地源_复制并校验大小()
    {
        using var temp = new TempDirectory();
        var package = Path.Combine(temp.Path, "pkg.bin");
        File.WriteAllBytes(package, new byte[4096]);

        File.WriteAllText(
            Path.Combine(temp.Path, "version.json"),
            $"{{\"version\":\"99.0\",\"url\":\"pkg.bin\",\"size\":4096}}");

        var service = new UpdateService(temp.Path, Path.Combine(temp.Path, "updates"));
        service.Settings.SourceUrl = Path.Combine(temp.Path, "version.json");
        service.Save();

        var check = await service.CheckAsync();
        var download = await service.DownloadAsync(check.Info!);

        Assert.True(download.Ok, download.Message);
        Assert.True(File.Exists(download.FilePath));
        Assert.Equal(4096, new FileInfo(download.FilePath).Length);
        Assert.Equal("ClassManager.App-99.0.exe", Path.GetFileName(download.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_大小不符_判定失败()
    {
        using var temp = new TempDirectory();
        var package = Path.Combine(temp.Path, "pkg.bin");
        File.WriteAllBytes(package, new byte[1024]);

        File.WriteAllText(
            Path.Combine(temp.Path, "version.json"),
            """{"version":"99.0","url":"pkg.bin","size":999999}""");

        var service = new UpdateService(temp.Path, Path.Combine(temp.Path, "updates"));
        service.Settings.SourceUrl = Path.Combine(temp.Path, "version.json");
        service.Save();

        var check = await service.CheckAsync();
        var download = await service.DownloadAsync(check.Info!);

        Assert.False(download.Ok);
        Assert.Contains("大小不符", download.Message);
    }

    [Fact]
    public void 设置可持久化往返()
    {
        using var temp = new TempDirectory();
        var store = new UpdateSettingsStore(temp.Path);

        var settings = store.Load();
        Assert.True(settings.AutoCheckOnStartup);
        Assert.Equal(string.Empty, settings.SourceUrl);

        settings.SourceUrl = "https://example.com/version.json";
        settings.AutoCheckOnStartup = false;
        settings.IgnoredVersion = "0.19";
        store.Save(settings);

        var reloaded = store.Load();
        Assert.Equal("https://example.com/version.json", reloaded.SourceUrl);
        Assert.False(reloaded.AutoCheckOnStartup);
        Assert.Equal("0.19", reloaded.IgnoredVersion);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cm-update-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
