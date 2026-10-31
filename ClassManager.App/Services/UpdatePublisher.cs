using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassManager.App.Services;

/// <summary>
/// 生成「更新源」：把打包好的安装包复制到更新源目录，并写一份 version.json 清单。
/// <para>
/// 「检查更新」功能本身早就实现了（读更新源上的 version.json → 比版本 → 下载 → 覆盖安装），
/// 但此前缺了这一环：没有任何东西生成 version.json，用户得自己手写。
/// 这个类把最后一环补上，让「打包 → 放更新源 → 应用里点检查更新」能真正跑通。
/// </para>
/// </summary>
public static class UpdatePublisher
{
    /// <summary>
    /// 实测可用的 GitHub 加速前缀（2026-10 实测：369–516 KB/s，国内直连单连接只有 14 KB/s）。
    /// 会写进清单的 mirrors 数组，程序在直连失败时按顺序回退。
    /// </summary>
    private static readonly string[] AcceleratorPrefixes =
    {
        "https://api.gitproxy.dev/",
        "https://ghfast.top/",
    };

    /// <summary>默认本地更新源目录（用户自己的机器上最省事：放共享盘/网盘同步目录也行）。</summary>
    public static string DefaultSourceDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClassManager",
        "update-source");

    /// <summary>清单文件名。</summary>
    public const string ManifestFileName = "version.json";

    /// <summary>
    /// 发布一次更新：复制安装包 + 生成清单。
    /// </summary>
    /// <param name="packagePath">打包好的单文件 exe 路径。</param>
    /// <param name="sourceDirectory">更新源目录（不存在会创建）。</param>
    /// <param name="notes">更新说明。</param>
    /// <param name="version">版本号；为空时取程序集版本。</param>
    /// <returns>生成结果描述（供 CLI / 提示框显示）。</returns>
    public static string Publish(
        string packagePath,
        string? sourceDirectory = null,
        string? notes = null,
        string? version = null,
        string? pageUrl = null,
        string? githubRepo = null,
        IReadOnlyList<string>? extraMirrors = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return $"找不到安装包：{packagePath}";
        }

        var directory = string.IsNullOrWhiteSpace(sourceDirectory)
            ? DefaultSourceDirectory
            : sourceDirectory;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            return $"无法创建更新源目录：{ex.Message}";
        }

        var fileName = Path.GetFileName(packagePath);
        var target = Path.Combine(directory, fileName);

        try
        {
            // 同名文件可能正被占用（自己就在运行），先删再拷
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Copy(packagePath, target);
        }
        catch (Exception ex)
        {
            return $"复制安装包到更新源失败：{ex.Message}";
        }

        var size = new FileInfo(target).Length;
        var sha = ComputeSha256(target);
        var effectiveVersion = string.IsNullOrWhiteSpace(version) ? AppVersion.Current : version.Trim();

        // GitHub 模式：下载地址指向 Releases 附件，下载页指向 Releases 列表页
        var isGitHub = !string.IsNullOrWhiteSpace(githubRepo);
        var downloadUrl = fileName;
        var releasePage = pageUrl ?? string.Empty;
        var mirrors = new List<string>();

        if (isGitHub)
        {
            var repo = githubRepo!.Trim().Trim('/');
            downloadUrl = $"https://github.com/{repo}/releases/download/v{effectiveVersion}/{fileName}";
            releasePage = $"https://github.com/{repo}/releases/latest";

            // 加速镜像：这些前缀把「跨境那一段」交给它们境内的节点，
            // 实测 369–516 KB/s，而国内直连单连接只有 14 KB/s。
            // 程序在直连失败时会按顺序回退到这些地址。
            foreach (var prefix in AcceleratorPrefixes)
            {
                mirrors.Add(prefix + downloadUrl);
            }
        }

        foreach (var extra in extraMirrors ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                mirrors.Add(extra.Trim());
            }
        }

        var manifest = new JsonObject
        {
            ["version"] = effectiveVersion,
            ["url"] = downloadUrl,
            ["size"] = size,
            ["sha256"] = sha,
            ["notes"] = notes ?? string.Empty,
            ["publishedAt"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        if (mirrors.Count > 0)
        {
            var array = new JsonArray();

            foreach (var mirror in mirrors)
            {
                array.Add(mirror);
            }

            manifest["mirrors"] = array;
        }

        // 下载页（可选）：填了它，检查到新版后「打开下载页」会用浏览器打开这个地址
        if (!string.IsNullOrWhiteSpace(releasePage))
        {
            manifest["page"] = releasePage.Trim();
        }

        var manifestPath = Path.Combine(directory, ManifestFileName);

        try
        {
            // 注意：必须以 JsonSerializerOptions.Default 为基底复制一份。
            // 直接 new 出来的 options 没有 TypeInfoResolver，
            // 序列化含 JsonArray 的清单时会抛
            //「must specify a TypeInfoResolver setting before being marked as read-only」。
            var jsonOptions = new JsonSerializerOptions(JsonSerializerOptions.Default)
            {
                WriteIndented = true,

                // 中文保持原样，别转义成 \uXXXX（这份文件用户是要打开看的）
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            File.WriteAllText(
                manifestPath,
                manifest.ToJsonString(jsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return $"写更新清单失败：{ex.Message}";
        }

        var message = $"已发布更新源：{directory}\n"
                      + $"  版本：{effectiveVersion}\n"
                      + $"  安装包：{fileName}（{size / 1024.0 / 1024.0:F1} MB）\n"
                      + $"  清单：{manifestPath}\n"
                      + $"\n在「关于 → 更新源设置」里把更新源填成：\n{manifestPath}";

        if (isGitHub)
        {
            var repo = githubRepo!.Trim().Trim('/');
            message += "\n\n【GitHub 模式】把这份 version.json 提交到仓库（建议放 docs/ 目录），"
                       + "然后把更新源填成下面这个地址（jsDelivr 读小文件实测 1.8 秒，raw.githubusercontent 实测 20 秒超时，别用）：\n"
                       + $"  https://cdn.jsdelivr.net/gh/{repo}@main/docs/version.json\n"
                       + $"\n发版步骤：\n"
                       + $"  1. 在 GitHub 上建 Release，tag 填 v{effectiveVersion}\n"
                       + $"  2. 把 {fileName} 传上去当附件\n"
                       + $"  3. 把 docs/version.json 提交到仓库（版本号、sha256 每次都要更新）\n"
                       + $"\n下载页：{releasePage}"
                       + $"\n加速镜像：{mirrors.Count} 个（直连失败会自动依次回退）";
        }
        else if (!string.IsNullOrWhiteSpace(pageUrl))
        {
            message += $"\n下载页：{pageUrl}";
        }

        return message;
    }

    /// <summary>默认更新源清单路径。</summary>
    public static string DefaultManifestPath => Path.Combine(DefaultSourceDirectory, ManifestFileName);

    /// <summary>默认更新源是否已经就绪（存在清单）。</summary>
    public static bool HasDefaultSource => File.Exists(DefaultManifestPath);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
