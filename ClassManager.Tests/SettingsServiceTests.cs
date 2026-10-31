using System.IO;
using System.Text.Json;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>设置服务单元测试。</summary>
public class SettingsServiceTests
{
    [Fact]
    public void Defaults_MatchSpecification()
    {
        var settings = new AppSettings();

        Assert.Equal(ThemePreference.Light, settings.Theme);
        Assert.True(settings.GenerateDemoScores);
        Assert.Null(settings.LastImportDirectory);
        Assert.Null(settings.LastExportDirectory);
        Assert.Equal(1400, settings.WindowWidth);
        Assert.Equal(800, settings.WindowHeight);
        Assert.True(double.IsNaN(settings.WindowLeft));
        Assert.True(double.IsNaN(settings.WindowTop));
        Assert.NotNull(settings.Author);
    }

    [Fact]
    public void Author_Defaults_MatchOriginal()
    {
        var author = AuthorInfo.Default();

        Assert.Equal("K.", author.Name);
        Assert.Equal("1.0", author.Version);
        Assert.Equal("2026年1月10日", author.ReleaseDate);
        Assert.Equal("ThatYearSV@outlook.com", author.Email);
        Assert.Equal("none", author.Website);
        Assert.Contains("点名系统", author.Description);
    }

    [Fact]
    public void SettingsFilePath_IsUnderAppDataClassManager()
    {
        var service = new SettingsService();

        Assert.EndsWith("settings.json", service.SettingsFilePath);
        Assert.Contains("ClassManager", service.SettingsFilePath);
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var original = new AppSettings
        {
            Theme = ThemePreference.Dark,
            GenerateDemoScores = false,
            LastImportDirectory = "C:/data",
            LastExportDirectory = "C:/out",
            WindowWidth = 1600,
            WindowHeight = 900,
            WindowLeft = 100,
            WindowTop = 120,
            Author = new AuthorInfo { Name = "Tester", Version = "2.0" },
        };

        var json = JsonSerializer.Serialize(original, SettingsService.JsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(ThemePreference.Dark, restored!.Theme);
        Assert.False(restored.GenerateDemoScores);
        Assert.Equal("C:/data", restored.LastImportDirectory);
        Assert.Equal(1600, restored.WindowWidth);
        Assert.Equal(100, restored.WindowLeft);
        Assert.Equal("Tester", restored.Author.Name);
        Assert.Equal("2.0", restored.Author.Version);
    }

    [Fact]
    public void RoundTrip_PreservesNaNWindowPosition()
    {
        var original = new AppSettings();
        Assert.True(double.IsNaN(original.WindowLeft));

        var json = JsonSerializer.Serialize(original, SettingsService.JsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions);

        Assert.NotNull(restored);
        Assert.True(double.IsNaN(restored!.WindowLeft));
    }

    [Fact]
    public void Current_IsNeverNullAfterLoad()
    {
        var service = new SettingsService();
        var path = service.SettingsFilePath;
        var dir = Path.GetDirectoryName(path)!;

        var hadDir = Directory.Exists(dir);
        var hadSettings = File.Exists(path);
        var original = hadSettings ? File.ReadAllText(path) : null;

        try
        {
            // 在「无设置文件」的确定环境下验证加载后的文档化默认值（避免依赖真实用户状态）
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            service.Load();

            // 非仅“非空”：加载后应具备文档化默认值
            Assert.NotNull(service.Current);
            Assert.Equal(ThemePreference.Light, service.Current.Theme);
            Assert.True(service.Current.GenerateDemoScores);
            Assert.Equal(1400, service.Current.WindowWidth);
            Assert.Equal(800, service.Current.WindowHeight);
        }
        finally
        {
            if (original is not null)
            {
                File.WriteAllText(path, original);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!hadDir && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    [Fact]
    public void Load_MigratesSystemThemeToLight_AndRewritesFile()
    {
        var service = new SettingsService();
        var path = service.SettingsFilePath;
        var dir = Path.GetDirectoryName(path)!;

        var hadDir = Directory.Exists(dir);
        var hadSettings = File.Exists(path);
        var original = hadSettings ? File.ReadAllText(path) : null;

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ \"Theme\": \"System\" }");

            service.Load();

            // 迁移：System → Light
            Assert.Equal(ThemePreference.Light, service.Current.Theme);

            // 文件被回写：不再含 "System"，改为 "Light"
            var rewritten = File.ReadAllText(path);
            Assert.DoesNotContain("System", rewritten, StringComparison.Ordinal);
            Assert.Contains("Light", rewritten, StringComparison.Ordinal);
        }
        finally
        {
            if (original is not null)
            {
                File.WriteAllText(path, original);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!hadDir && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaultsAndBacksUp()
    {
        var service = new SettingsService();
        var path = service.SettingsFilePath;
        var dir = Path.GetDirectoryName(path)!;
        var backup = Path.Combine(dir, "settings.bak.json");

        var hadDir = Directory.Exists(dir);
        var hadSettings = File.Exists(path);
        var original = hadSettings ? File.ReadAllText(path) : null;
        var hadBackup = File.Exists(backup);
        var originalBackup = hadBackup ? File.ReadAllText(backup) : null;

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not valid json ###");
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            service.Load();

            // 优雅降级：不抛异常、回落到默认值、并将损坏文件备份
            Assert.NotNull(service.Current);
            Assert.Equal(ThemePreference.Light, service.Current.Theme);
            Assert.True(service.Current.GenerateDemoScores);
            Assert.True(File.Exists(backup), "损坏的设置文件应被备份为 settings.bak.json");
        }
        finally
        {
            if (original is not null)
            {
                File.WriteAllText(path, original);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (originalBackup is not null)
            {
                File.WriteAllText(backup, originalBackup);
            }
            else if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            if (!hadDir && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }
}
