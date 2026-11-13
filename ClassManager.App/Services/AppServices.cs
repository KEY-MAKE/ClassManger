using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>
/// 静态组合根：手工装配全部服务单例 + 共享状态（当前名单 / 班级名 / 设置）。
/// 明确不使用 MS DI 容器。
/// </summary>
public static class AppServices
{
    public static IImportService Import { get; private set; } = null!;

    public static IRollCallService RollCall { get; private set; } = null!;

    public static ISeatingService Seating { get; private set; } = null!;

    public static IExportService Export { get; private set; } = null!;

    public static ISettingsService Settings { get; private set; } = null!;

    public static IThemeService Theme { get; private set; } = null!;

    public static IShortcutService Shortcut { get; private set; } = null!;

    public static IInstallService Install { get; private set; } = null!;

    public static ISingleInstanceService SingleInstance { get; private set; } = null!;

    public static IWindowActivationService WindowActivation { get; private set; } = null!;

    /// <summary>评讲课件生成服务（内嵌 Python 引擎）。</summary>
    public static IExam2PptService Exam2Ppt { get; private set; } = null!;

    /// <summary>AI 办公：对话服务（内置 key，自动轮换）。</summary>
    public static IAiChatService Ai { get; private set; } = null!;

    /// <summary>AI 办公：本地设置与会话存储。</summary>
    public static AiStore AiStore { get; private set; } = null!;

    /// <summary>检查更新（更新源 + 下载 + 交给新包覆盖安装）。</summary>
    public static IUpdateService Update { get; private set; } = null!;

    /// <summary>
    /// 更新下载的后台管理者（应用级）。
    /// 下载不绑在「关于」窗口上 —— 窗口关了要继续下，重新打开要能看到进度。
    /// </summary>
    public static UpdateDownloadManager UpdateDownload { get; private set; } = null!;

    public static NavigationPageProvider PageProvider { get; private set; } = null!;

    /// <summary>是否已完成装配。</summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>当前名单（共享状态）。</summary>
    public static IReadOnlyList<Student> CurrentStudents { get; private set; } = Array.Empty<Student>();

    /// <summary>当前班级名（共享状态）。</summary>
    public static string CurrentClassName { get; private set; } = string.Empty;

    /// <summary>名单被替换（导入成功）后触发，携带完整导入结果。</summary>
    public static event EventHandler<ImportResult>? RosterImported;

    /// <summary>装配全部服务（幂等）。</summary>
    public static void Initialize()
    {
        if (IsInitialized)
        {
            return;
        }

        Settings = new SettingsService();
        Settings.Load();

        Theme = new ThemeService();
        Import = new ImportService();
        RollCall = new RollCallService();
        Seating = new SeatingService();
        Export = new ExportService();
        Shortcut = new ShortcutService();
        Install = new InstallService(Shortcut, () => Settings.Current.Author);
        SingleInstance = new SingleInstanceService();
        WindowActivation = new WindowActivationService();
        Exam2Ppt = new Exam2PptService();
        Ai = new AiChatService();
        AiStore = new AiStore();
        Update = new UpdateService();
        UpdateDownload = new UpdateDownloadManager(Update);
        PageProvider = new NavigationPageProvider();

        IsInitialized = true;
    }

    /// <summary>应用一次导入结果：更新共享状态 + 载入点名/排座服务 + 广播事件。</summary>
    public static void ApplyRoster(ImportResult result)
    {
        CurrentStudents = result.Students;
        CurrentClassName = result.ClassName;

        RollCall.LoadRoster(result.Students);
        Seating.LoadRoster(result.Students);

        // 落盘一份，下次启动直接可用（导入和启动恢复都会走这里，内容一致，重复保存无害）
        RosterStore.Save(result);

        RosterImported?.Invoke(null, result);
    }
}
