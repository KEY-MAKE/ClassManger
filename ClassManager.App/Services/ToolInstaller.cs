using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClassManager.App.Services;

/// <summary>
/// Agent 的「自我扩展」能力：联网检索工具 → 安装到工作区 → 直接调用。
/// <para>
/// 安装一律只往工作区的 <c>_tools/</c> 里放，不污染系统 Python / Node；
/// 每次安装前都会把「包名 + 来源 + 实际命令」交给用户确认。
/// </para>
/// </summary>
public sealed class ToolInstaller
{
    private readonly string _workspace;

    public ToolInstaller(string workspace)
    {
        _workspace = workspace;
    }

    /// <summary>工具安装目录（工作区内）。</summary>
    public string ToolsDirectory => Path.Combine(_workspace, "_tools");

    /// <summary>Python 包安装目录。</summary>
    public string PythonToolsDirectory => Path.Combine(ToolsDirectory, "python");

    /// <summary>Node 包安装目录。</summary>
    public string NodeToolsDirectory => Path.Combine(ToolsDirectory, "node");

    /// <summary>
    /// 可用的 Python 解释器。
    /// <para>
    /// <b>优先用程序自带的嵌入式 Python</b>（课件引擎里那份）。
    /// 老师们的电脑通常没装 Python —— 只查 PATH 的话，AI 办公的
    /// <c>run_python</c> 在别人机器上直接不可用，而在开发机上一切正常。
    /// </para>
    /// <para>其次才是系统 Python（带 pip，能装包）。</para>
    /// </summary>
    public static string? FindPython()
    {
        var bundled = FindBundledPython();

        if (bundled is not null)
        {
            return bundled;
        }

        foreach (var candidate in new[] { "python.exe", "python3.exe" })
        {
            var path = FindOnPath(candidate);
            if (path is not null)
            {
                return path;
            }
        }

        // %LOCALAPPDATA%\Programs\Python\Python3xx\python.exe
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installsRoot = Path.Combine(localAppData, "Programs", "Python");

        if (Directory.Exists(installsRoot))
        {
            var candidates = Directory.GetDirectories(installsRoot)
                .Where(d => Path.GetFileName(d).StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

            foreach (var dir in candidates)
            {
                var exe = Path.Combine(dir, "python.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        // Microsoft Store 版（WindowsApps 里是转发器，也能用）
        var storePython = Path.Combine(localAppData, "Microsoft", "WindowsApps", "python.exe");
        if (File.Exists(storePython))
        {
            return storePython;
        }

        // 退到 py 启动器
        var launcher = Path.Combine(localAppData, "Programs", "Python", "Launcher", "py.exe");
        return File.Exists(launcher) ? launcher : null;
    }

    /// <summary>
    /// 找程序自带的嵌入式 Python（课件引擎解压出来的 <c>runtime/python.exe</c>）。
    /// <para>
    /// 路径：<c>%LocalAppData%\ClassManager\engine\exam2ppt-*\runtime\python.exe</c>。
    /// 引擎可能还没解压（用户没用过课件生成），这时返回 null，
    /// 调用方可以触发 <c>Exam2PptService.EnsureEngineAsync</c> 来准备。
    /// </para>
    /// </summary>
    public static string? FindBundledPython()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassManager",
                "engine");

            if (!Directory.Exists(root))
            {
                return null;
            }

            // 可能残留多个版本目录，取最新的那个
            return Directory
                .GetDirectories(root, "exam2ppt-*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "runtime", "python.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>可用的 Node。</summary>
    public static string? FindNode() => FindOnPath("node.exe") ?? FindOnPath("node");

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // 忽略
            }
        }

        return null;
    }

    /// <summary>联网检索：同时查 PyPI（精确包名）与 Bing（关键词）。</summary>
    public async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "query 不能为空。";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"【检索：{query}】");

        var pypi = await QueryPyPiAsync(query.Trim(), ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(pypi))
        {
            builder.AppendLine();
            builder.AppendLine("── PyPI ──");
            builder.AppendLine(pypi);
        }

        var web = await SearchWebAsync(query.Trim(), ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(web))
        {
            builder.AppendLine();
            builder.AppendLine("── 网页结果 ──");
            builder.AppendLine(web);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0
            ? "没找到相关内容。可以换个关键词，或直接用 install_tool 装 PyPI 上的包。"
            : result;
    }

    /// <summary>查 PyPI 上的包（精确名或按关键词匹配）。</summary>
    private async Task<string> QueryPyPiAsync(string name, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();

            var url = $"https://pypi.org/pypi/{Uri.EscapeDataString(name)}/json";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var node = JsonNode.Parse(body);

            var info = node?["info"];
            if (info is null)
            {
                return string.Empty;
            }

            var latest = info["version"]?.GetValue<string>() ?? "?";
            var summary = info["summary"]?.GetValue<string>() ?? string.Empty;
            var home = info["home_page"]?.GetValue<string>() ?? info["project_url"]?.GetValue<string>() ?? string.Empty;
            var requires = info["requires_dist"] is JsonArray requiresArray
                ? string.Join(", ", requiresArray.Select(v => v?.GetValue<string>() ?? string.Empty).Take(6))
                : string.Empty;

            return $"包名：{info["name"]?.GetValue<string>()}\n"
                   + $"最新版本：{latest}\n"
                   + $"简介：{summary}\n"
                   + $"主页：{home}\n"
                   + $"依赖：{(requires.Length == 0 ? "无" : requires)}";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>用 Bing 做关键词检索（HTML → 文本 + 链接）。</summary>
    private async Task<string> SearchWebAsync(string query, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();

            var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Bing 的结果块：<li class="b_algo"> ... <h2><a href="URL">TITLE</a></h2> ... <p>SNIPPET</p>
            var builder = new StringBuilder();
            var count = 0;

            foreach (Match block in Regex.Matches(html, @"<li class=""b_algo"".*?</li>", RegexOptions.Singleline))
            {
                var link = Regex.Match(block.Value, @"<h2>\s*<a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline);
                if (!link.Success)
                {
                    continue;
                }

                var title = Regex.Replace(link.Groups[2].Value, "<[^>]+>", string.Empty);
                var href = link.Groups[1].Value;

                var snippet = string.Empty;
                var p = Regex.Match(block.Value, @"<p[^>]*>(.*?)</p>", RegexOptions.Singleline);
                if (p.Success)
                {
                    snippet = Regex.Replace(p.Groups[1].Value, "<[^>]+>", string.Empty);
                }

                builder.AppendLine($"{count + 1}. {WebUtility.HtmlDecode(title.Trim())}");
                builder.AppendLine($"   {href}");

                if (snippet.Trim().Length > 0)
                {
                    builder.AppendLine($"   {WebUtility.HtmlDecode(snippet.Trim())}");
                }

                count++;

                if (count >= 6)
                {
                    break;
                }
            }

            return builder.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>安装 Python 包到工作区（pip install --target，不污染系统）。</summary>
    public async Task<string> InstallPythonPackageAsync(string package, CancellationToken ct)
    {
        if (!IsValidPackageName(package))
        {
            return $"包名不合法：{package}（只允许字母、数字、点、下划线、连字符）";
        }

        var python = FindPython();
        if (python is null)
        {
            return "系统里找不到 Python，无法安装 Python 包。";
        }

        // 内置的嵌入式 Python 是裁剪版（没有 pip / ensurepip），装不了第三方包。
        // 放在包名校验之后判断 —— 非法包名要优先被拦下来。
        if (string.Equals(python, FindBundledPython(), StringComparison.OrdinalIgnoreCase))
        {
            return "当前使用的是程序内置的 Python（精简版，不含 pip），无法安装第三方包。"
                   + "请改用标准库完成任务，或直接用 write_xlsx / write_docx / write_pptx 这些原生工具生成文件。";
        }

        Directory.CreateDirectory(PythonToolsDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var (code, output, error) = await RunPipInstallAsync(startInfo, package, ct).ConfigureAwait(false);

        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            if (detail.Length > 500)
            {
                detail = detail[..500] + "…";
            }

            return $"安装 {package} 失败（退出码 {code}）：{detail}";
        }

        return $"已安装 Python 包 {package} → {PythonToolsDirectory}\n"
               + $"之后用 run_python 执行脚本时，这个包可以直接 import。";
    }

    /// <summary>
    /// 执行 pip install。用户本机常把源指到国内镜像，若镜像被网络策略挡住会报
    ///「from versions: none」，这时自动回退到 PyPI 官方源重试一次。
    /// </summary>
    private async Task<(int Code, string Output, string Error)> RunPipInstallAsync(
        ProcessStartInfo startInfo,
        string package,
        CancellationToken ct)
    {
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("pip");
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--disable-pip-version-check");
        startInfo.ArgumentList.Add("--no-input");
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(PythonToolsDirectory);
        startInfo.ArgumentList.Add(package);

        var result = await RunProcessAsync(startInfo, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        if (result.Code == 0)
        {
            return result;
        }

        var looksLikeIndexProblem =
            result.Error.Contains("from versions: none", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("No matching distribution", StringComparison.OrdinalIgnoreCase)
            || result.Output.Contains("from versions: none", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeIndexProblem)
        {
            return result;
        }

        // 换个源再来一次
        var retry = new ProcessStartInfo
        {
            FileName = startInfo.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        retry.ArgumentList.Add("-m");
        retry.ArgumentList.Add("pip");
        retry.ArgumentList.Add("install");
        retry.ArgumentList.Add("--disable-pip-version-check");
        retry.ArgumentList.Add("--no-input");
        retry.ArgumentList.Add("--index-url");
        retry.ArgumentList.Add("https://pypi.org/simple");
        retry.ArgumentList.Add("--target");
        retry.ArgumentList.Add(PythonToolsDirectory);
        retry.ArgumentList.Add(package);

        return await RunProcessAsync(retry, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
    }

    /// <summary>安装 Node 包到工作区。</summary>
    public async Task<string> InstallNodePackageAsync(string package, CancellationToken ct)
    {
        if (!IsValidPackageName(package))
        {
            return $"包名不合法：{package}（只允许字母、数字、点、下划线、连字符、@ 和 /）";
        }

        var npm = FindOnPath("npm.cmd") ?? FindOnPath("npm");
        if (npm is null)
        {
            return "系统里找不到 npm，无法安装 Node 包。";
        }

        Directory.CreateDirectory(NodeToolsDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = npm,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add("--no-fund");
        startInfo.ArgumentList.Add("--prefix");
        startInfo.ArgumentList.Add(NodeToolsDirectory);
        startInfo.ArgumentList.Add(package);

        var (code, output, error) = await RunProcessAsync(startInfo, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            if (detail.Length > 500)
            {
                detail = detail[..500] + "…";
            }

            return $"安装 {package} 失败（退出码 {code}）：{detail}";
        }

        return $"已安装 Node 包 {package} → {NodeToolsDirectory}\n"
               + $"之后用 run_node 执行脚本时可以直接 require 它。";
    }

    /// <summary>在已装好工具的环境里跑一段 Python 脚本。</summary>
    public async Task<string> RunPythonAsync(string code, CancellationToken ct)
    {
        var python = FindPython();
        if (python is null)
        {
            return "系统里找不到 Python。";
        }

        var script = Path.Combine(Path.GetTempPath(), $"cm_run_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(script, code, new UTF8Encoding(false), ct).ConfigureAwait(false);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = _workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            startInfo.ArgumentList.Add(script);

            // 让刚装的包可以被 import
            if (Directory.Exists(PythonToolsDirectory))
            {
                startInfo.Environment["PYTHONPATH"] = PythonToolsDirectory;
            }

            var (exitCode, output, error) = await RunProcessAsync(startInfo, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);

            var text = $"退出码 {exitCode}\n";
            if (!string.IsNullOrWhiteSpace(output))
            {
                text += "--- 标准输出 ---\n" + Truncate(output, 8000) + "\n";
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                text += "--- 标准错误 ---\n" + Truncate(error, 4000);
            }

            return text.Trim();
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>在已装好工具的环境里跑一段 Node 脚本。</summary>
    public async Task<string> RunNodeAsync(string code, CancellationToken ct)
    {
        var node = FindNode();
        if (node is null)
        {
            return "系统里找不到 Node。";
        }

        var script = Path.Combine(Path.GetTempPath(), $"cm_run_{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(script, code, new UTF8Encoding(false), ct).ConfigureAwait(false);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = node,
                WorkingDirectory = _workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            startInfo.ArgumentList.Add(script);

            if (Directory.Exists(NodeToolsDirectory))
            {
                startInfo.Environment["NODE_PATH"] = NodeToolsDirectory;
            }

            var (exitCode, output, error) = await RunProcessAsync(startInfo, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);

            var text = $"退出码 {exitCode}\n";
            if (!string.IsNullOrWhiteSpace(output))
            {
                text += "--- 标准输出 ---\n" + Truncate(output, 8000) + "\n";
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                text += "--- 标准错误 ---\n" + Truncate(error, 4000);
            }

            return text.Trim();
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static async Task<(int Code, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (-1, string.Empty, "无法启动进程");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略
            }

            var partial = await SafeRead(outputTask).ConfigureAwait(false);
            return (-2, partial, "超时已终止");
        }

        var output = await SafeRead(outputTask).ConfigureAwait(false);
        var error = await SafeRead(errorTask).ConfigureAwait(false);

        return (process.ExitCode, output, error);
    }

    private static async Task<string> SafeRead(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClassManagerAgent/1.0");
        return client;
    }

    /// <summary>包名白名单：防止把命令参数混进包名里。</summary>
    private static bool IsValidPackageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            return false;
        }

        return Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._\-]*$")
               || Regex.IsMatch(name, @"^@[A-Za-z0-9\-]+/[A-Za-z0-9._\-]+$");
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";
}
