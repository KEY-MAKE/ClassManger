using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>一次写入操作的确认请求。</summary>
public sealed class AiWriteRequest
{
    public string FullPath { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool IsNewFile { get; init; }
    public long ExistingLength { get; init; }
}

/// <summary>
/// 提供给模型的本地文件工具。默认沙箱在「工作区」内；全盘模式由设置放开。
/// 写操作在「仅工作区」模式下必须先经用户确认。
/// </summary>
public sealed class AiFileTools
{
    private const int MaxReadBytes = 64 * 1024;

    private readonly AiSettings _settings;
    private readonly Func<AiWriteRequest, Task<bool>> _confirmWrite;

    public AiFileTools(AiSettings settings, Func<AiWriteRequest, Task<bool>> confirmWrite)
    {
        _settings = settings;
        _confirmWrite = confirmWrite;
    }

    /// <summary>工具声明。</summary>
    public static IReadOnlyList<AiToolSpec> Specs { get; } = new List<AiToolSpec>
    {
        new()
        {
            Name = "list_files",
            Description = "列出某个目录下的文件与子目录。path 可为相对工作区的路径或绝对路径。",
            ParametersJson = """
            {"type":"object","properties":{"path":{"type":"string","description":"目录路径，留空表示工作区根目录"}},"required":["path"]}
            """,
        },
        new()
        {
            Name = "read_file",
            Description = "读取文件内容。支持：文本/代码/CSV/JSON（纯文本）、Excel（xlsx/xls，渲染成文本）、Word（docx）、PPT（pptx）。"
                          + "图片与其它二进制文件会明确拒绝并说明原因，绝不会返回乱码——遇到这种提示时请向用户索取文本版本。",
            ParametersJson = """
            {"type":"object","properties":{"path":{"type":"string","description":"文件路径"}},"required":["path"]}
            """,
        },
        new()
        {
            Name = "write_file",
            Description = "写入/覆盖一个文本文件（父目录不存在会自动创建）。修改用户文件前需用户确认。",
            ParametersJson = """
            {"type":"object","properties":{"path":{"type":"string","description":"文件路径"},"content":{"type":"string","description":"完整的新内容"}},"required":["path","content"]}
            """,
        },
        new()
        {
            Name = "write_xlsx",
            Description = "生成 Excel 工作簿（.xlsx）。参数：path 输出路径；sheets 对象，"
                        + "形如 {\"成绩\":[{\"姓名\":\"张三\",\"分数\":90}]}，第一行自动作为表头。",
            ParametersJson = """
            {"type":"object","properties":{"path":{"type":"string","description":"输出文件路径，建议以 .xlsx 结尾"},"sheets":{"type":"object","description":"工作表集合，键是表名，值是行数组（每行是一个对象）","additionalProperties":{"type":"array","items":{"type":"object"}}}},"required":["path","sheets"]}
            """,
        },
        new()
        {
            Name = "write_docx",
            Description = "生成 Word 文档（.docx）。参数：path 输出路径；content 用 Markdown 写正文，"
                        + "# 标题、- 列表会自动转成标题与项目符号。",
            ParametersJson = """
            {"type":"object","properties":{"path":{"type":"string","description":"输出文件路径，建议以 .docx 结尾"},"content":{"type":"string","description":"正文，用 Markdown 写：# 标题，- 列表"}},"required":["path","content"]}
            """,
        },
        new()
        {
            Name = "write_pptx",
            Description = "生成 PowerPoint（.pptx）。参数：path 输出路径；content 用 Markdown 写正文，"
                        + "每行 # 标题算一页、- 列表是该页的要点。",
            ParametersJson = """
            {"type":"object","properties":{"path":{"type":"string","description":"输出文件路径，建议以 .pptx 结尾"},"content":{"type":"string","description":"正文，用 Markdown 写：每行 # 标题算一页，- 是该页要点"}},"required":["path","content"]}
            """,
        },
        new()
        {
            Name = "search_tool",
            Description = "联网检索可用的工具/库。当现有工具做不了用户的要求时，先用它找出合适的包。"
                        + "参数：query 关键词或 PyPI 包名。返回 PyPI 包信息 + 网页结果摘要。",
            ParametersJson = """
            {"type":"object","properties":{"query":{"type":"string","description":"检索关键词或 PyPI 包名"}},"required":["query"]}
            """,
        },
        new()
        {
            Name = "install_tool",
            Description = "安装检索到的工具包（装到工作区 _tools/，不污染系统）。"
                        + "参数：package 包名；kind 可选 \"python\"（默认）或 \"node\"。安装前会请用户确认。",
            ParametersJson = """
            {"type":"object","properties":{"package":{"type":"string","description":"要安装的包名，如 pypdf、pdfplumber（npm 包用 @scope/name 形式）"},"kind":{"type":"string","enum":["python","node"],"description":"包类型，默认 python"}},"required":["package"]}
            """,
        },
        new()
        {
            Name = "run_python",
            Description = "执行一段 Python 脚本（可以用 import 使用刚装好的包）。参数：code 脚本内容。",
            ParametersJson = """
            {"type":"object","properties":{"code":{"type":"string","description":"要执行的 Python 脚本源码"}},"required":["code"]}
            """,
        },
        new()
        {
            Name = "run_node",
            Description = "执行一段 Node.js 脚本（可以用 require 使用刚装好的包）。参数：code 脚本内容。",
            ParametersJson = """
            {"type":"object","properties":{"code":{"type":"string","description":"要执行的 Node.js 脚本源码"}},"required":["code"]}
            """,
        },
        new()
        {
            Name = "web_fetch",
            Description = "联网抓取网址并返回正文文本（HTML 自动转纯文本）。参数：url 完整地址（http/https）。",
            ParametersJson = """
            {"type":"object","properties":{"url":{"type":"string","description":"完整网址，必须以 http:// 或 https:// 开头"}},"required":["url"]}
            """,
        },
        new()
        {
            Name = "create_directory",
            Description = "新建一个目录（已存在则忽略）。",
            ParametersJson = """
            {"type":"object","properties":{"path":{"type":"string","description":"目录路径"}},"required":["path"]}
            """,
        },
    };

    /// <summary>执行一个工具调用，返回给模型的文本结果。</summary>
    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
    {
        JsonNode? args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (Exception ex)
        {
            return $"参数不是合法 JSON：{ex.Message}";
        }

        var rawPath = args?["path"]?.GetValue<string>() ?? string.Empty;

        try
        {
            return name switch
            {
                "list_files" => ListFiles(rawPath),
                "read_file" => ReadFile(rawPath),
                "write_file" => await WriteFileAsync(rawPath, args?["content"]?.GetValue<string>() ?? string.Empty, ct)
                    .ConfigureAwait(false),
                "write_xlsx" => await WriteXlsxAsync(rawPath, args?["sheets"], ct).ConfigureAwait(false),
                "write_docx" => await WriteOfficeAsync(rawPath, args?["content"]?.GetValue<string>() ?? string.Empty, "docx", ct).ConfigureAwait(false),
                "write_pptx" => await WriteOfficeAsync(rawPath, args?["content"]?.GetValue<string>() ?? string.Empty, "pptx", ct).ConfigureAwait(false),
                "web_fetch" => await FetchWebAsync(args?["url"]?.GetValue<string>() ?? string.Empty, ct).ConfigureAwait(false),
                "search_tool" => await SearchToolAsync(args?["query"]?.GetValue<string>() ?? string.Empty, ct).ConfigureAwait(false),
                "install_tool" => await InstallToolAsync(
                    args?["package"]?.GetValue<string>() ?? string.Empty,
                    args?["kind"]?.GetValue<string>() ?? "python",
                    ct).ConfigureAwait(false),
                "run_python" => await RunPythonAsync(args?["code"]?.GetValue<string>() ?? string.Empty, ct).ConfigureAwait(false),
                "run_node" => await RunNodeAsync(args?["code"]?.GetValue<string>() ?? string.Empty, ct).ConfigureAwait(false),
                "create_directory" => CreateDirectory(rawPath),
                _ => $"未知工具：{name}",
            };
        }
        catch (Exception ex)
        {
            return $"工具执行失败：{ex.Message}";
        }
    }

    private string ListFiles(string path)
    {
        var full = ResolvePath(path, allowEmptyAsRoot: true);
        if (!Directory.Exists(full))
        {
            return $"目录不存在：{full}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"目录：{full}");

        foreach (var dir in Directory.GetDirectories(full).OrderBy(d => d).Take(200))
        {
            builder.AppendLine($"[目录] {Path.GetFileName(dir)}");
        }

        foreach (var file in Directory.GetFiles(full).OrderBy(f => f).Take(200))
        {
            var info = new FileInfo(file);
            builder.AppendLine($"[文件] {info.Name}\t{info.Length:N0} 字节");
        }

        return builder.ToString();
    }

    private string ReadFile(string path)
    {
        var full = ResolvePath(path);

        // 统一走 AiDocumentReader：docx / pptx / xlsx 提取文字，二进制明确拒绝，
        // 绝不把原始字节当文本返回（否则模型看到的就是一屏乱码）。
        var outcome = AiDocumentReader.Read(full);

        if (outcome.Kind == AiDocumentReader.Kind.Missing)
        {
            return outcome.Text;
        }

        if (!outcome.Ok)
        {
            return outcome.Text;
        }

        return outcome.Text;
    }

    private async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var full = ResolvePath(path);

        // 别用 write_file 写图片/二进制（写出来是坏文件），引导用对的工具
        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico" or ".pdf"
            or ".docx" or ".pptx" or ".xlsx" or ".xls" or ".exe" or ".dll" or ".zip" or ".7z")
        {
            var hint = extension is ".docx" or ".pptx" or ".xlsx" or ".xls"
                ? "请改用 write_docx / write_pptx / write_xlsx 生成。"
                : extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico"
                    ? "当前不支持生成图片，请改生成文本、表格或文档。"
                    : "该格式不支持用文本写入。";

            return $"write_file 只能写文本文件，不能写 {extension} 格式。{hint}";
        }

        var isNew = !File.Exists(full);
        var existingLength = isNew ? 0 : new FileInfo(full).Length;

        // 仅工作区模式：写入前必须确认
        if (_settings.PermissionMode == AiPermissionMode.WorkspaceOnly)
        {
            var approved = await _confirmWrite(new AiWriteRequest
            {
                FullPath = full,
                Content = content,
                IsNewFile = isNew,
                ExistingLength = existingLength,
            }).ConfigureAwait(false);

            if (!approved)
            {
                return "用户拒绝了这次写入，文件未修改。";
            }
        }

        ct.ThrowIfCancellationRequested();

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct).ConfigureAwait(false);

        return isNew
            ? $"已创建文件：{full}（{Encoding.UTF8.GetByteCount(content):N0} 字节）"
            : $"已覆盖文件：{full}（原 {existingLength:N0} 字节 → 新 {Encoding.UTF8.GetByteCount(content):N0} 字节）";
    }

    /// <summary>生成 Excel（用 MiniExcel，键即表名，列表首行作表头）。</summary>
    private async Task<string> WriteXlsxAsync(string path, JsonNode? sheetsNode, CancellationToken ct)
    {
        var full = ResolvePath(path);

        if (sheetsNode is not JsonObject sheets)
        {
            return "参数 sheets 必须是对象，例如 {\"成绩\":[{\"姓名\":\"张三\",\"分数\":90}]}";
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var preview = new StringBuilder();

        foreach (var sheet in sheets)
        {
            var rows = new List<Dictionary<string, object>>();

            if (sheet.Value is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not JsonObject row)
                    {
                        continue;
                    }

                    var cells = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var cell in row)
                    {
                        cells[cell.Key] = ToPlainValue(cell.Value);
                    }

                    rows.Add(cells);
                }
            }

            data[sheet.Key] = rows;
            preview.AppendLine($"工作表「{sheet.Key}」：{rows.Count} 行");
        }

        if (data.Count == 0)
        {
            return "sheets 里没有任何工作表。";
        }

        var approved = await ConfirmWriteAsync(full, preview.ToString(), ct).ConfigureAwait(false);
        if (!approved)
        {
            return "用户拒绝了这次写入，文件未修改。";
        }

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        MiniExcelLibs.MiniExcel.SaveAs(full, data, printHeader: true);

        var size = new FileInfo(full).Length;
        return $"已生成 Excel：{full}（{data.Count} 个工作表，{size:N0} 字节）";
    }

    /// <summary>生成 Word / PPT（走引擎自带的 python-docx / python-pptx）。</summary>
    private async Task<string> WriteOfficeAsync(string path, string content, string mode, CancellationToken ct)
    {
        var full = ResolvePath(path);

        if (string.IsNullOrWhiteSpace(content))
        {
            return "content 不能为空（请用 Markdown 写正文）。";
        }

        var approved = await ConfirmWriteAsync(full, content, ct).ConfigureAwait(false);
        if (!approved)
        {
            return "用户拒绝了这次写入，文件未修改。";
        }

        var engineRoot = await EnsureEngineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(engineRoot))
        {
            return "内置文档引擎不可用（无法解压 Python 运行时），请先到「评讲课件」里跑一次生成。";
        }

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return mode == "docx"
            ? await OfficeWriter.WriteDocxAsync(engineRoot, content, full, ct).ConfigureAwait(false)
            : await OfficeWriter.WritePptxAsync(engineRoot, content, full, ct).ConfigureAwait(false);
    }

    /// <summary>联网抓取网页并返回可读文本。</summary>
    private async Task<string> FetchWebAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "url 不能为空。";
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "url 必须以 http:// 或 https:// 开头。";
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClassManagerAgent/1.0");

            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return $"抓取失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{Truncate(body, 300)}";
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var text = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? HtmlToText(body)
                : body;

            if (text.Length > 30000)
            {
                text = text[..30000] + "\n…（内容过长，已截断）";
            }

            return $"抓取成功：{url}\n\n{text.Trim()}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"抓取失败：{ex.Message}";
        }
    }

    /// <summary>联网检索可用工具（PyPI + 网页搜索）。</summary>
    private async Task<string> SearchToolAsync(string query, CancellationToken ct)
    {
        var installer = CreateInstaller();
        return await installer.SearchAsync(query, ct).ConfigureAwait(false);
    }

    /// <summary>安装工具包到工作区（安装前必须确认）。</summary>
    private async Task<string> InstallToolAsync(string package, string kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            return "package 不能为空。";
        }

        var installer = CreateInstaller();
        var isNode = kind.Equals("node", StringComparison.OrdinalIgnoreCase);

        var command = isNode
            ? $"npm install --prefix \"{installer.NodeToolsDirectory}\" {package}"
            : $"python -m pip install --target \"{installer.PythonToolsDirectory}\" {package}";

        var approved = await _confirmWrite(new AiWriteRequest
        {
            FullPath = isNode ? installer.NodeToolsDirectory : installer.PythonToolsDirectory,
            Content = $"将要安装工具包：{package}\n来源：{(isNode ? "npm 官方源" : "PyPI 官方源")}\n命令：{command}",
            IsNewFile = true,
            ExistingLength = 0,
        }).ConfigureAwait(false);

        if (!approved)
        {
            return "用户拒绝了这次安装，没有安装任何东西。";
        }

        return isNode
            ? await installer.InstallNodePackageAsync(package, ct).ConfigureAwait(false)
            : await installer.InstallPythonPackageAsync(package, ct).ConfigureAwait(false);
    }

    /// <summary>执行 Python 脚本。</summary>
    private async Task<string> RunPythonAsync(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "code 不能为空。";
        }

        // 内置 Python 在课件引擎里，用户没用过「试卷转课件」时还没解压。
        // 这里顺手把它准备出来（首次约几秒），否则老师们的电脑上
        // （没装 Python）会直接报「找不到解释器」。
        if (ToolInstaller.FindPython() is null)
        {
            var engine = AppServices.Exam2Ppt;
            var ready = await engine.EnsureEngineAsync(null, ct).ConfigureAwait(false);

            if (!ready)
            {
                return "当前电脑没有可用的 Python 运行环境，且内置运行时准备失败："
                       + (engine.LastEngineError ?? "未知原因")
                       + "\n请改用不依赖 Python 的方式完成任务（比如直接用写文件工具生成结果）。";
            }
        }

        return await CreateInstaller().RunPythonAsync(code, ct).ConfigureAwait(false);
    }

    /// <summary>执行 Node 脚本。</summary>
    private async Task<string> RunNodeAsync(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "code 不能为空。";
        }

        // 程序只自带 Python，不带 Node（node.exe 约 80MB，为体积考虑没打包）。
        // 明确告诉模型「这条路走不通」，让它改用 Python 或直接写文件，
        // 而不是让它反复重试或给用户报一个看不懂的错误。
        if (ToolInstaller.FindNode() is null)
        {
            return "本机没有 Node.js 运行环境（程序只自带 Python，不含 Node）。"
                   + "请改用 run_python，或者直接用写文件工具生成结果文件。";
        }

        return await CreateInstaller().RunNodeAsync(code, ct).ConfigureAwait(false);
    }

    private ToolInstaller CreateInstaller()
    {
        var workspace = string.IsNullOrWhiteSpace(_settings.WorkspaceDirectory)
            ? AiSettings.DefaultWorkspace
            : _settings.WorkspaceDirectory;

        return new ToolInstaller(workspace);
    }

    /// <summary>HTML → 纯文本（去掉脚本样式与标签，保留段落换行）。</summary>
    private static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style|noscript)[^>]*>.*?</\1>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|div|li|h[1-6]|tr|section|blockquote)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, "\n{3,}", "\n\n");

        return text.Trim();
    }

    private static object ToPlainValue(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            return value.ToString();
        }

        if (node is JsonArray array)
        {
            return string.Join("、", array.Select(ToPlainValue));
        }

        return node.ToJsonString();
    }

    /// <summary>写入前的统一确认（仅工作区模式需要）。</summary>
    private async Task<bool> ConfirmWriteAsync(string fullPath, string content, CancellationToken ct)
    {
        if (_settings.PermissionMode != AiPermissionMode.WorkspaceOnly)
        {
            return true;
        }

        var isNew = !File.Exists(fullPath);
        var existingLength = isNew ? 0 : new FileInfo(fullPath).Length;

        var approved = await _confirmWrite(new AiWriteRequest
        {
            FullPath = fullPath,
            Content = content,
            IsNewFile = isNew,
            ExistingLength = existingLength,
        }).ConfigureAwait(false);

        return approved;
    }

    /// <summary>确保内置引擎（含 Python 运行时）已解压，返回引擎根目录。</summary>
    private async Task<string> EnsureEngineAsync(CancellationToken ct)
    {
        try
        {
            var engine = AppServices.Exam2Ppt;
            var ok = await engine.EnsureEngineAsync(null, ct).ConfigureAwait(false);
            return ok ? engine.EngineRoot : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";

    private string CreateDirectory(string path)
    {
        var full = ResolvePath(path);
        if (Directory.Exists(full))
        {
            return $"目录已存在：{full}";
        }

        Directory.CreateDirectory(full);
        return $"已创建目录：{full}";
    }

    /// <summary>把模型给的路径解析为绝对路径，并做权限校验。</summary>
    private string ResolvePath(string path, bool allowEmptyAsRoot = false)
    {
        var workspace = string.IsNullOrWhiteSpace(_settings.WorkspaceDirectory)
            ? AiSettings.DefaultWorkspace
            : _settings.WorkspaceDirectory;

        string candidate;

        if (string.IsNullOrWhiteSpace(path))
        {
            if (!allowEmptyAsRoot)
            {
                throw new InvalidOperationException("path 不能为空。");
            }

            candidate = workspace;
        }
        else if (Path.IsPathRooted(path))
        {
            candidate = path;
        }
        else
        {
            candidate = Path.Combine(workspace, path);
        }

        var full = Path.GetFullPath(candidate);

        if (_settings.PermissionMode == AiPermissionMode.FullDisk)
        {
            return full;
        }

        var root = Path.GetFullPath(workspace);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var inside = full.Equals(root, StringComparison.OrdinalIgnoreCase)
                     || full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);

        if (!inside)
        {
            throw new InvalidOperationException(
                $"路径越界：{full} 不在工作区 {root} 内。如需访问其他位置，请在 AI 办公页把权限切换为「全盘」。");
        }

        return full;
    }
}
