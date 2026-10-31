namespace ClassManager.App.Services;

/// <summary>快捷方式服务接口（WScript.Shell COM 晚绑定）。</summary>
public interface IShortcutService
{
    /// <summary>创建 .lnk 快捷方式。</summary>
    void CreateLink(
        string shortcutPath,
        string targetExe,
        string workingDirectory,
        string iconPath,
        string description);

    /// <summary>删除快捷方式（不存在时忽略）。</summary>
    void DeleteLink(string shortcutPath);
}
