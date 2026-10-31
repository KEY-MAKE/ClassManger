using System.Text.Json;
using System.IO;

namespace ClassManager.App.Services;

/// <summary>更新设置的本地存储（%AppData%\ClassManager\update.json）。</summary>
public sealed class UpdateSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;

    /// <param name="baseDirectory">存储目录；传 null 用 <c>%AppData%\ClassManager</c>。</param>
    public UpdateSettingsStore(string? baseDirectory = null)
    {
        _dir = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassManager")
            : baseDirectory;
    }

    public string SettingsPath => Path.Combine(_dir, "update.json");

    /// <summary>读取设置（无文件/损坏时返回默认值）。</summary>
    public UpdateSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(SettingsPath), Options)
                       ?? new UpdateSettings();
            }
        }
        catch
        {
            // 损坏则回落默认
        }

        return new UpdateSettings();
    }

    /// <summary>保存设置。</summary>
    public void Save(UpdateSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 忽略
        }
    }
}
