using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>设置服务接口。</summary>
public interface ISettingsService
{
    /// <summary>设置文件完整路径（%AppData%\ClassManager\settings.json）。</summary>
    string SettingsFilePath { get; }

    /// <summary>当前设置。</summary>
    AppSettings Current { get; }

    /// <summary>读取设置（失败回落默认值）。</summary>
    void Load();

    /// <summary>写入设置。</summary>
    void Save();
}
