using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>
/// 设置实现：%AppData%\ClassManager\settings.json（System.Text.Json）。
/// 读取失败时回落默认值并把原文件备份为 settings.bak.json。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    /// <summary>序列化选项：缩进、枚举字符串、允许 NaN 等特殊浮点字面量。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClassManager");
        SettingsFilePath = Path.Combine(dir, "settings.json");
    }

    /// <inheritdoc />
    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                Current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            Current = loaded ?? new AppSettings();

            // 迁移：新版不再「跟随系统」。历史配置中的 System → 固定浅色，并回写文件（老用户升级后即固定浅色）。
            if (Current.Theme == ThemePreference.System)
            {
                Current.Theme = ThemePreference.Light;
                Save();
            }
        }
        catch
        {
            TryBackup();
            Current = new AppSettings();
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // 设置持久化失败不应影响主流程（禁止静默失败由调用方另行提示）。
        }
    }

    private void TryBackup()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return;
            }

            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            File.Copy(SettingsFilePath, Path.Combine(dir, "settings.bak.json"), true);
        }
        catch
        {
            // 备份失败可忽略
        }
    }
}
