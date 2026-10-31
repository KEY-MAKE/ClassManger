using System.IO;
using System.Text.Json;

namespace ClassManager.App.Services;

/// <summary>评讲课件的本地设置。</summary>
public sealed class Exam2PptSettings
{
    /// <summary>自定义背景图路径；为空表示用引擎自带的默认背景。</summary>
    public string BackgroundImagePath { get; set; } = string.Empty;
}

/// <summary>评讲课件设置存储（独立小文件，不污染既有 settings.json）。</summary>
public sealed class Exam2PptSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;

    /// <param name="baseDirectory">存储目录；传 null 用 <c>%AppData%\ClassManager</c>。</param>
    public Exam2PptSettingsStore(string? baseDirectory = null)
    {
        _dir = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassManager")
            : baseDirectory;
    }

    public string SettingsPath => Path.Combine(_dir, "exam2ppt.json");

    /// <summary>读取设置（无文件时返回默认值）。</summary>
    public Exam2PptSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<Exam2PptSettings>(File.ReadAllText(SettingsPath), Options)
                       ?? new Exam2PptSettings();
            }
        }
        catch
        {
            // 损坏则回落默认
        }

        return new Exam2PptSettings();
    }

    /// <summary>保存设置。</summary>
    public void Save(Exam2PptSettings settings)
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
