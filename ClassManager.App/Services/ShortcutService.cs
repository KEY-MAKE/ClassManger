using System.IO;
using System.Runtime.InteropServices;

namespace ClassManager.App.Services;

/// <summary>
/// 快捷方式实现：使用 Windows 自带的 WScript.Shell COM（晚绑定），
/// 不引入任何 NuGet 依赖。
/// </summary>
public sealed class ShortcutService : IShortcutService
{
    /// <inheritdoc />
    public void CreateLink(
        string shortcutPath,
        string targetExe,
        string workingDirectory,
        string iconPath,
        string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建 WScript.Shell 实例（COM 可能被安全策略禁用）。");

        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 WScript.Shell 实例。");

        try
        {
            dynamic dynamicShell = shell;
            dynamic link = dynamicShell.CreateShortcut(shortcutPath);
            link.TargetPath = targetExe;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = iconPath + ",0";
            link.Description = description;
            link.Save();
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    /// <inheritdoc />
    public void DeleteLink(string shortcutPath)
    {
        try
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
        }
        catch
        {
            // 删除失败时忽略（可能被占用），卸载流程继续。
        }
    }
}
