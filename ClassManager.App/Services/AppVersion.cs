using System.Reflection;

namespace ClassManager.App.Services;

/// <summary>
/// 应用版本信息（单一来源：程序集 <c>InformationalVersion</c>，由 csproj 的 &lt;Version&gt; 决定）。
/// 版本号体系：0.1 → 0.11 → 0.12 …，界面统一显示为 v0.1 / v0.11。
/// </summary>
public static class AppVersion
{
    /// <summary>当前版本号，如 "0.1"、"0.11"。</summary>
    public static string Current { get; } = Resolve();

    /// <summary>界面显示用版本号，如 "v0.1"。</summary>
    public static string Display => "v" + Current;

    private static string Resolve()
    {
        // 用**本程序集自身**，而不是 Assembly.GetEntryAssembly()。
        // 入口程序集在某些宿主下不是我们（比如被测试宿主加载时是 testhost.exe），
        // 那会读到别人的版本号 —— 版本号一错，"检查更新"的比较结果就全错了。
        var assembly = typeof(AppVersion).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 去掉可能存在的 +commit 后缀
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.1" : $"{version.Major}.{version.Minor}";
    }
}
