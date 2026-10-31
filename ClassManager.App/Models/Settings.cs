namespace ClassManager.App.Models;

/// <summary>主题偏好。</summary>
public enum ThemePreference
{
    /// <summary>跟随系统。</summary>
    System,

    /// <summary>强制浅色。</summary>
    Light,

    /// <summary>强制深色。</summary>
    Dark,
}

/// <summary>作者信息（保留原「编辑作者信息」功能，作为「关于」的次级入口）。</summary>
public sealed class AuthorInfo
{
    public string Name { get; set; } = "K.";
    public string Version { get; set; } = "1.0";
    public string ReleaseDate { get; set; } = "2026年1月10日";
    public string Email { get; set; } = "ThatYearSV@outlook.com";
    public string Website { get; set; } = "none";
    public string Description { get; set; } = "集成了点名系统和座位管理系统的班级综合管理工具";

    /// <summary>创建一份默认作者信息副本。</summary>
    public static AuthorInfo Default() => new();
}

/// <summary>安装选项。</summary>
public sealed class InstallOptions
{
    public string InstallDirectory { get; set; } = string.Empty;
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
}

/// <summary>应用设置（持久化到 %AppData%\ClassManager\settings.json）。</summary>
public sealed class AppSettings
{
    /// <summary>主题偏好。默认浅色（不再跟随系统）。</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.Light;
    public bool GenerateDemoScores { get; set; } = true;
    public string? LastImportDirectory { get; set; }
    public string? LastExportDirectory { get; set; }
    public string? InstallDirectory { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 800;

    /// <summary>窗口左边界；NaN 表示启动时居中。</summary>
    public double WindowLeft { get; set; } = double.NaN;

    /// <summary>窗口上边界；NaN 表示启动时居中。</summary>
    public double WindowTop { get; set; } = double.NaN;

    public AuthorInfo Author { get; set; } = new();
}
