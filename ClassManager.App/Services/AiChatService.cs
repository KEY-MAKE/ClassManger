using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>流式增量（<see cref="Status"/> 用于展示"正在重试"这类过程提示）。</summary>
public sealed record AiStreamUpdate(string? ContentDelta, string? ReasoningDelta, string? Status = null);

/// <summary>一次模型回合的结果。</summary>
public sealed class AiTurnResult
{
    public bool Ok { get; init; }

    /// <summary>
    /// 失败原因是「配额用尽」（所有 key / 所有候选模型都 429）。
    /// 界面据此提示用户换模型并停止任务，而不是让他对着转圈干等。
    /// </summary>
    public bool QuotaExhausted { get; init; }
    public string Content { get; init; } = string.Empty;
    public string Reasoning { get; init; } = string.Empty;
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
    public AiUsageTotals Usage { get; init; } = new();
    public string? Error { get; init; }
    public string KeyLabel { get; init; } = string.Empty;

    /// <summary>
    /// 服务端的结束原因。length = 输出被 max_tokens 截断（不是模型说完了）。
    /// </summary>
    public string FinishReason { get; init; } = string.Empty;

    /// <summary>
    /// 正文里出现了 DSML 工具调用标记（网关转换器偶发泄漏）。
    /// true 表示这一轮模型本该调工具，但标记没被还原成结构化调用。
    /// </summary>
    public bool DsmlLeak { get; init; }

    /// <summary>
    /// 这一轮只有内部推理、没有可读正文（网关把思考写进了 content，
    /// 或模型陷入纯思考循环只吐 &lt;result&gt; 标记）。
    /// </summary>
    public bool ReasoningOnly { get; init; }

    /// <summary>内部标记数量异常，疑似空转循环。</summary>
    public bool MarkerLoop { get; init; }
}

/// <summary>供模型使用的工具声明。</summary>
public sealed class AiToolSpec
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>JSON Schema 形式的参数定义。</summary>
    public string ParametersJson { get; init; } = "{\"type\":\"object\",\"properties\":{}}";
}

/// <summary>
/// AI 对话服务：调用 OpenAI 兼容的 /chat/completions。
/// 内置两把 key，api1 优先；遇到 401/403/429 或 5xx 自动切 api2 重试。
/// </summary>
public interface IAiChatService
{
    /// <summary>模型名（展示用）。</summary>
    string ModelName { get; }

    /// <summary>当前使用的 key 标签（api1 / api2）。</summary>
    string ActiveKeyLabel { get; }

    /// <summary>最近一次成功调用实际用的模型。</summary>
    string LastUsedModel { get; }

    /// <summary>配置里的首选模型（界面上「当前模型」初始显示它）。</summary>
    string PrimaryModel { get; }

    /// <summary>
    /// 可选模型目录（带消耗标注）。空列表表示配置里没写 models，
    /// 界面应退化成「只显示当前模型」。
    /// </summary>
    IReadOnlyList<AiModelOption> AvailableModels { get; }

    /// <summary>
    /// 用户手动指定的模型 id。设为非空后 <b>只走这一个模型、不再自动 fallback</b> ——
    /// 用户既然挑了，就该让他自己决定换不换；配额用完时提示他换，而不是偷偷换掉。
    /// 设为空则回到「首选模型 + 备用链」的自动模式。
    /// </summary>
    string PreferredModel { get; set; }

    /// <summary>本次运行累计用量。</summary>
    AiUsageTotals SessionUsage { get; }

    /// <summary>执行一个模型回合（流式）。</summary>
    Task<AiTurnResult> CompleteAsync(
        IReadOnlyList<AiMessage> history,
        IReadOnlyList<AiToolSpec> tools,
        IProgress<AiStreamUpdate>? stream,
        CancellationToken ct);
}

/// <summary>内嵌凭据。</summary>
/// <summary>可选模型（带消耗标注，界面上给用户挑）。</summary>
public sealed class AiModelOption
{
    /// <summary>API 里的模型 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>消耗档位：1=低 2=中 3=高。</summary>
    public int Cost { get; init; } = 1;

    /// <summary>一句话说明。</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>消耗标签。</summary>
    public string CostLabel => Cost switch
    {
        >= 3 => "高消耗",
        2 => "中消耗",
        _ => "低消耗",
    };

    /// <summary>下拉框里显示的文字。</summary>
    public string Display => string.IsNullOrWhiteSpace(Note)
        ? $"{Label}　[{CostLabel}]"
        : $"{Label}　[{CostLabel}]　{Note}";
}

public sealed class AiCredentials
{
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>可选模型目录（空则退化成「只有 Model + Fallbacks」的老行为）。</summary>
    public List<AiModelOption> Models { get; init; } = new();

    /// <summary>首选模型。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// 备用模型：主模型的所有 key 都配额用尽/限流时，按顺序换着试。
    /// <para>
    /// 这个平台是按模型分配配额的 —— deepseek 用完了，别的模型往往还有额度。
    /// 没有这一层的话，主模型一 429 整个 AI 办公就瘫了。
    /// </para>
    /// </summary>
    public List<string> Fallbacks { get; init; } = new();

    public List<string> Keys { get; init; } = new();
}

public sealed class AiChatService : IAiChatService
{
    private const string CredentialResourceSuffix = "ai.credentials.json";

    private readonly HttpClient _http;
    private AiCredentials _credentials;
    private int _activeKeyIndex;
    private string _preferredModel = string.Empty;
    private bool _lastQuotaExhausted;
    private readonly List<AiModelOption> _availableModels = new();

    public AiChatService()
    {
        _credentials = LoadCredentials();
        BuildAvailableModels();

        // 万一服务端对响应做了 gzip/deflate/br 压缩而没被解压，读到的就是乱码；
        // 显式开启自动解压，把这种可能性排除掉。
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_credentials.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    }

    /// <inheritdoc />
    public string ModelName => _credentials.Model;

    /// <inheritdoc />
    public string ActiveKeyLabel => $"api{_activeKeyIndex + 1}";

    /// <summary>
    /// 重新读凭据（用户在设置里换了自己的 key 之后调用）。
    /// </summary>
    public void ReloadCredentials()
    {
        try
        {
            _credentials = LoadCredentials();
            _activeKeyIndex = 0;
            _lastErrorSummary = string.Empty;
            BuildAvailableModels();
        }
        catch
        {
            // 读失败就继续用旧的，不能让 AI 功能整个挂掉
        }
    }

    /// <summary>
    /// 把凭据里的模型目录铺到 <see cref="AvailableModels"/>。
    /// 没配 models 的老格式就退化成「首选 + 备用」，保证下拉框不为空。
    /// </summary>
    private void BuildAvailableModels()
    {
        _availableModels.Clear();

        if (_credentials.Models.Count > 0)
        {
            _availableModels.AddRange(_credentials.Models);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_credentials.Model))
        {
            _availableModels.Add(new AiModelOption
            {
                Id = _credentials.Model,
                Label = _credentials.Model,
                Cost = 1,
            });
        }

        foreach (var fallback in _credentials.Fallbacks)
        {
            if (!string.IsNullOrWhiteSpace(fallback)
                && !_availableModels.Exists(m => string.Equals(m.Id, fallback, StringComparison.OrdinalIgnoreCase)))
            {
                _availableModels.Add(new AiModelOption
                {
                    Id = fallback,
                    Label = fallback,
                    Cost = 1,
                });
            }
        }
    }

    /// <summary>最近一次成功调用实际用的模型（主模型配额耗尽时会变成 fallback 那个）。</summary>
    public string LastUsedModel { get; private set; } = string.Empty;

    /// <inheritdoc />
    public string PrimaryModel => _credentials.Model;

    /// <inheritdoc />
    public IReadOnlyList<AiModelOption> AvailableModels => _availableModels;

    /// <inheritdoc />
    public string PreferredModel
    {
        get => _preferredModel;
        set => _preferredModel = value ?? string.Empty;
    }

    /// <inheritdoc />
    public AiUsageTotals SessionUsage { get; } = new();

    /// <inheritdoc />
    public async Task<AiTurnResult> CompleteAsync(
        IReadOnlyList<AiMessage> history,
        IReadOnlyList<AiToolSpec> tools,
        IProgress<AiStreamUpdate>? stream,
        CancellationToken ct)
    {
        if (_credentials.Keys.Count == 0)
        {
            return new AiTurnResult { Ok = false, Error = "未找到内置 API Key。" };
        }

        // 模型轮换：这个平台按「模型」分配配额，主模型用完了别的模型往往还有额度。
        // 不轮换的话，deepseek 一 429 整个 AI 办公就瘫了。
        var models = new List<string>();

        if (!string.IsNullOrWhiteSpace(_preferredModel))
        {
            // 用户手选了模型 → 只走它，不自动换。
            // 换掉的话用户会觉得「我明明选了，怎么偷偷换了」；
            // 配额用完应该提示他自己换（QuotaExhausted 标志）。
            models.Add(_preferredModel);
        }
        else
        {
            // 自动模式：首选 + 备用链
            if (!string.IsNullOrWhiteSpace(_credentials.Model))
            {
                models.Add(_credentials.Model);
            }

            foreach (var fallback in _credentials.Fallbacks)
            {
                if (!string.IsNullOrWhiteSpace(fallback)
                    && !models.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                {
                    models.Add(fallback);
                }
            }
        }

        if (models.Count == 0)
        {
            models.Add(string.Empty);
        }

        var modelErrors = new List<string>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _lastQuotaExhausted = false;

        for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            var model = models[modelIndex];
            var body = BuildRequestBody(history, tools, model);

            var outcome = await TryAllKeysAsync(body, stream, ct).ConfigureAwait(false);

            if (outcome is not null)
            {
                LastUsedModel = model;

                AiUsageLog.Write(
                    true,
                    model,
                    outcome.KeyLabel,
                    stopwatch.Elapsed,
                    outcome.Usage.PromptTokens,
                    outcome.Usage.CompletionTokens);

                return outcome;
            }

            modelErrors.Add(model);

            // 还有下一个模型 → 换着试
            if (modelIndex < models.Count - 1)
            {
                stream?.Report(new AiStreamUpdate(
                    null,
                    null,
                    $"{model} 配额用尽或不可用，换用 {models[modelIndex + 1]} 重试…"));
            }
        }

        AiUsageLog.Write(
            false,
            string.Join(",", modelErrors),
            "-",
            stopwatch.Elapsed,
            0,
            0,
            _lastErrorSummary);

        return new AiTurnResult
        {
            Ok = false,
            QuotaExhausted = _lastQuotaExhausted,
            Error = _lastQuotaExhausted
                ? "所选模型（" + string.Join(" / ", modelErrors) + "）的配额已经用尽。\n"
                  + "请在上方模型下拉框里换一个模型再试。\n" + _lastErrorSummary
                : "所有模型与 API Key 都不可用（" + string.Join(" / ", modelErrors) + "）。\n"
                  + _lastErrorSummary,
        };
    }

    /// <summary>
    /// 用同一个请求体把所有 key 试一遍；全部失败返回 null（交给上层换模型）。
    /// </summary>
    private async Task<AiTurnResult?> TryAllKeysAsync(
        string body,
        IProgress<AiStreamUpdate>? stream,
        CancellationToken ct)
    {
        // 每把 key 最多尝试 3 次（限流是"通道级"的，隔一会儿通常会放开，直接切 key 往往两个都凉）
        const int maxAttemptsPerKey = 3;
        var backoff = new[] { 3000, 8000 };

        var errors = new List<string>();
        var keyCount = _credentials.Keys.Count;

        for (var keyOffset = 0; keyOffset < keyCount; keyOffset++)
        {
            var index = (_activeKeyIndex + keyOffset) % keyCount;
            var label = $"api{index + 1}";
            var key = _credentials.Keys[index];
            var switchKey = false;

            for (var attempt = 0; attempt < maxAttemptsPerKey && !switchKey; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var outcome = await TryOnceAsync(key, label, body, stream, ct).ConfigureAwait(false);

                if (outcome.Ok)
                {
                    _activeKeyIndex = index;
                    SessionUsage.Add(outcome.Usage);
                    return new AiTurnResult
                    {
                        Ok = true,
                        Content = outcome.Content,
                        Reasoning = outcome.Reasoning,
                        ToolCalls = outcome.ToolCalls,
                        FinishReason = outcome.FinishReason,
                        DsmlLeak = outcome.DsmlLeak,
                        ReasoningOnly = outcome.ReasoningOnly,
                        MarkerLoop = outcome.MarkerLoop,
                        Usage = outcome.Usage,
                        KeyLabel = label,
                    };
                }

                if (!outcome.SwitchKey)
                {
                    // 参数类错误：换 key 也没用，直接失败
                    return new AiTurnResult { Ok = false, Error = outcome.Error, KeyLabel = label };
                }

                errors.Add($"{label} 第{attempt + 1}次: {outcome.Error}");

                // 限流/繁忙 → 同一把 key 退避重试；鉴权/余额 → 立刻换 key
                //
                // 但「配额用尽」不要重试：这个平台按模型分配配额，配额是真没了，
                // 等 3 秒 8 秒都没用 —— 只有换模型才有救。原来 6 把 key × 3 次 ≈ 34 秒，
                // 砍掉之后几秒就能换到能用的模型。
                // 配额是「账号 + 模型」级别的：换成别的 key 照样 429，
                // 试 6 把 key 只会白烧 5 次请求。直接放弃这个模型，让上层换模型。
                if (outcome.RateLimited && IsQuotaExhausted(outcome.Error))
                {
                    // 记一笔「这次失败是配额原因」，上层据此告诉用户「换模型」而不是「检查网络」
                    _lastQuotaExhausted = true;
                    stream?.Report(new AiStreamUpdate(null, null, $"{label} 配额用尽…"));
                    return null;
                }

                // 只要出现过「非配额」类失败（网络、鉴权、超时），就不能再把整次失败
                // 归因为配额 —— 否则用户会被误导去换模型，换了也没用。
                if (!outcome.RateLimited || !IsQuotaExhausted(outcome.Error))
                {
                    _lastQuotaExhausted = false;
                }

                if (!outcome.Retryable || attempt == maxAttemptsPerKey - 1)
                {
                    switchKey = true;
                    stream?.Report(new AiStreamUpdate(null, null, $"{label} 不可用，切换到下一把 key…"));
                    break;
                }

                var wait = backoff[Math.Min(attempt, backoff.Length - 1)];
                stream?.Report(new AiStreamUpdate(
                    null,
                    null,
                    $"{label} {(outcome.RateLimited ? "通道限流" : "服务繁忙")}，{wait / 1000} 秒后重试（第 {attempt + 2}/{maxAttemptsPerKey} 次）…"));

                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }

        // 全部 key 都失败 → 记下原因，返回 null 让上层换模型
        _lastErrorSummary = errors.Count > 0
            ? string.Join("；", errors.TakeLast(2))
            : "无可用 Key。";

        return null;
    }

    /// <summary>最近一次所有 key 失败的摘要（给最终错误信息用）。</summary>
    private string _lastErrorSummary = string.Empty;

    /// <summary>这个错误是不是「配额真的用完了」（而不是瞬时拥塞）。</summary>
    private static bool IsQuotaExhausted(string? error) =>
        !string.IsNullOrEmpty(error)
        && (error.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || error.Contains("额度", StringComparison.OrdinalIgnoreCase)
            || error.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 系统提示词：告诉模型它是一个本地 agent、有哪些工具、工作区在哪。
    /// 少了这段，模型经常只会「嘴上说要做」而不真正调用工具。
    /// </summary>
    private static string BuildSystemPrompt()
    {
        var workspace = AiSettings.DefaultWorkspace;

        try
        {
            var settings = AppServices.AiStore?.LoadSettings();
            if (!string.IsNullOrWhiteSpace(settings?.WorkspaceDirectory))
            {
                workspace = settings!.WorkspaceDirectory;
            }
        }
        catch
        {
            // 取不到就用默认工作区
        }

        return $"""
                你是一个运行在「班级综合管理系统」里的本地 AI 助手，可以真正动手做事，不只是聊天。

                工作区：{workspace}
                （所有相对路径都相对于这个目录；需要时请先用 list_files 看看里面有什么）

                【试卷格式规范】
                工作区里有一份《试卷格式规范.md》，是程序内置的（不是用户上传的）。
                当用户说「按规范/按要求/按标准 改试卷」「把试卷整理成能生成课件的格式」
                「按这个格式改」这类话时，**先 read_file 读《试卷格式规范.md》**，
                严格照它执行，不要让用户再上传规范。
                如果工作区里没有这个文件，说明程序没释放成功，直接告诉用户即可。

                你可以使用的工具：
                - list_files：列出目录内容
                - read_file：读取文件。支持文本/代码/CSV/JSON、Excel(xlsx/xls)、Word(docx)、PPT(pptx)；
                  图片与其它二进制会被明确拒绝并说明原因（不要重复尝试，直接向用户说明）
                - write_file：写任意文本文件（txt/md/json/xml/yaml/csv/代码文件等）
                - write_xlsx：生成 Excel（参数 sheets 是「表名 → 行数组」的对象）
                - write_docx：生成 Word（内容用 Markdown 写）
                - write_pptx：生成 PPT（内容用 Markdown 写，每行 # 标题一页）
                - create_directory：创建目录（可以一次建多级）
                - web_search：联网搜索（必应），返回「标题 + 链接 + 摘要」。需要查资料先用它
                - web_fetch：抓取网页正文
                - search_tool：联网检索可用的库/工具（查 PyPI + 网页搜索）
                - pdf_to_word：把 PDF 转成 Word（纯离线，文件不上传）。用户要「PDF 转 Word」时直接用它，
                  首次会自动装转换引擎（约 50MB），装完再转。
                - install_tool：把检索到的包安装到工作区 _tools/（会先请用户确认）
                - run_python：执行一段 Python 脚本（可以 import 刚装好的包）
                - run_node：执行一段 Node 脚本（可以 require 刚装好的包）

                当现有工具做不到用户的要求时（缺库、缺能力、缺格式支持），按这个顺序自我扩展：
                1. search_tool 检索能用的包（别凭空编包名，先查）；
                2. install_tool 安装（用户拒绝就换个方案或直接问用户，不要反复重试）；
                3. run_python / run_node 用装好的工具把活干完；
                4. 只从 PyPI / npm 安装（程序已配好清华源 / npmmirror 镜像），不要去装来路不明的脚本。

                输出规则（很重要）：
                - 只输出给用户看的内容。你的思考过程不要写出来。
                - 不要输出任何形如 <result>、｜DSML｜ 的内部标记，那类内容会被系统剥掉并让对话出错。
                - 每一轮都要么给出可读的回答，要么发起工具调用，不要两样都不给。

                主动联网（重要）：
                你的记忆可能过时，遇到下面这些情况**先用 web_search 查，不要凭印象编**：
                - 用户问「最新 / 现在 / 推荐哪个」这类问题（软件版本、价格、政策、教程）
                - 你不确定的事实（命令参数、报错含义、某个工具怎么用）
                - 用户提出操作类需求（「帮我清理磁盘」「优化开机速度」「找出占空间的文件」）——
                  先搜当前 Windows 的正确做法和要避开的坑，再动手，不要凭想象直接写脚本。
                搜到结果后，需要细节再用 web_fetch 打开具体页面。

                涉及删除文件时要格外小心：
                - 先列清单让用户看清楚要删什么，别直接删；
                - 带删除操作的脚本会先弹确认框给用户过目（这是设计如此，不是你出错了）；
                - 永远不要删系统目录、用户主目录、桌面、下载、文档这些地方。

                长任务要先列计划（重要）：
                如果一件事需要多步才能完成（「把这学期的成绩整理成报表」「把这份试卷改成课件」
                「帮我清理磁盘」「分析这批数据」这类），**先给一个 3~6 条的计划**：

                > 我打算这么做：
                > 1. …
                > 2. …
                > 3. …

                然后**逐条执行**，每完成一条就在回复里标一句进度（例如「第 2 步完成：已提取 45 个文件」）。
                不要闷头做十几分钟才说话 —— 用户不知道你在干什么会以为卡死了。
                简单问题（一句话能答、一次工具调用能解决）不要列计划，直接做。

                工作方式：
                1. 需要动手就调工具，不要只描述步骤。
                2. 输出文件前先把目录建好（create_directory），再写文件。
                3. 生成 Word/PPT 时用 Markdown：# 是标题，- 是列表条目。
                4. 工具返回「用户拒绝」时不要重试同一个操作，也不要直接结束任务；
                   要向用户说明你需要什么才能继续（换目录？让他点允许？），然后等用户回复。
                5. 最后用几句话说清你做了什么、产物在哪。
                """;
    }

    private string BuildRequestBody(
        IReadOnlyList<AiMessage> history,
        IReadOnlyList<AiToolSpec> tools,
        string model)
    {
        var messages = new JsonArray();

        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = BuildSystemPrompt(),
        });

        foreach (var message in history)
        {
            var node = new JsonObject { ["role"] = message.Role };

            if (message.Role == "tool")
            {
                node["tool_call_id"] = message.ToolCallId ?? string.Empty;
                // 带 tool_calls 时规范要求 content 为 null 或省略；空串不是规范值
                node["content"] = message.Content.Length == 0 && message.ToolCalls is { Count: > 0 }
                    ? null
                    : message.Content;
            }
            else
            {
                node["content"] = message.Content;

                if (message.ToolCalls is { Count: > 0 })
                {
                    var calls = new JsonArray();
                    foreach (var call in message.ToolCalls)
                    {
                        calls.Add(new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.Arguments,
                            },
                        });
                    }

                    node["tool_calls"] = calls;
                }
            }

            messages.Add(node);
        }

        var root = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            // 推理型模型会先消耗思考 token，给小上限会导致正文为空
            ["max_tokens"] = 16384,
            ["stream"] = true,
        };

        if (tools.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJson),
                    },
                });
            }

            root["tools"] = toolArray;
            root["tool_choice"] = "auto";
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private sealed class AttemptOutcome
    {
        public bool Ok { get; init; }

        /// <summary>是否换下一把 key 再试（鉴权/余额/限流/5xx 都属于此类）。</summary>
        public bool SwitchKey { get; init; }

        /// <summary>同一把 key 重试是否可能成功（限流、5xx、网络抖动）。</summary>
        public bool Retryable { get; init; }

        /// <summary>是否因通道限流失败（用于生成更友好的提示）。</summary>
        public bool RateLimited { get; init; }

        public string? Error { get; init; }
        public string Content { get; init; } = string.Empty;
        public string Reasoning { get; init; } = string.Empty;
        public List<AiToolCall> ToolCalls { get; init; } = new();
        public AiUsageTotals Usage { get; init; } = new();
        public string FinishReason { get; init; } = string.Empty;
        public bool DsmlLeak { get; init; }
        public bool ReasoningOnly { get; init; }
        public bool MarkerLoop { get; init; }
    }

    private async Task<AttemptOutcome> TryOnceAsync(
        string key,
        string keyLabel,
        string body,
        IProgress<AiStreamUpdate>? stream,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AttemptOutcome
            {
                Ok = false,
                SwitchKey = true,
                Retryable = true,
                Error = $"网络错误：{ex.Message}",
            };
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await SafeReadAsync(response, ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;

                var switchKey = response.StatusCode is HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.PaymentRequired
                    || code >= 500;

                // 限流 / 5xx 值得在同一把 key 上等一会儿再试；鉴权与余额问题换 key 才有意义
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || code >= 500;
                var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;

                var reason = code switch
                {
                    401 => "鉴权失败（key 无效）",
                    403 => "无权限",
                    429 => "通道限流",
                    402 => "余额不足",
                    _ => code >= 500 ? "服务端异常" : $"HTTP {code}",
                };

                return new AttemptOutcome
                {
                    Ok = false,
                    SwitchKey = switchKey,
                    Retryable = retryable,
                    RateLimited = rateLimited,
                    Error = $"{reason}：{ExtractErrorMessage(detail)}",
                };
            }

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var finishReason = string.Empty;
            var toolCalls = new SortedDictionary<int, ToolCallBuilder>();
            var usage = new AiUsageTotals();

            using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]")
                {
                    if (payload == "[DONE]")
                    {
                        break;
                    }

                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(payload);
                }
                catch
                {
                    continue;
                }

                if (node?["usage"] is JsonNode usageNode)
                {
                    usage = ParseUsage(usageNode);
                }

                var choices = node?["choices"] as JsonArray;
                if (choices is not { Count: > 0 })
                {
                    continue;
                }

                // 结束原因：length = 被 max_tokens 截断（不是模型说完了）
                if (choices[0]?["finish_reason"] is JsonValue finishValue
                    && finishValue.TryGetValue<string>(out var finishText)
                    && !string.IsNullOrEmpty(finishText))
                {
                    finishReason = finishText;
                }

                var delta = choices[0]?["delta"];
                if (delta is null)
                {
                    // 有些网关把 finish_reason 放在没有 delta 的那条上，不能直接 continue 掉
                    if (string.IsNullOrEmpty(finishReason))
                    {
                        continue;
                    }

                    continue;
                }

                var contentDelta = delta["content"]?.GetValue<string>();
                var reasoningDelta = delta["reasoning_content"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(contentDelta))
                {
                    content.Append(contentDelta);
                }

                if (!string.IsNullOrEmpty(reasoningDelta))
                {
                    reasoning.Append(reasoningDelta);
                }

                if (delta["tool_calls"] is JsonArray toolDeltas)
                {
                    foreach (var item in toolDeltas)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        var index = item["index"]?.GetValue<int>() ?? toolCalls.Count;
                        if (!toolCalls.TryGetValue(index, out var builder))
                        {
                            builder = new ToolCallBuilder();
                            toolCalls[index] = builder;
                        }

                        if (item["id"]?.GetValue<string>() is { Length: > 0 } id)
                        {
                            builder.Id = id;
                        }

                        var function = item["function"];
                        if (function?["name"]?.GetValue<string>() is { Length: > 0 } name)
                        {
                            builder.Name = name;
                        }

                        // arguments 标准是 JSON 字符串；但网关偶尔给对象，这里两种都收
                        var argumentsNode = function?["arguments"];
                        if (argumentsNode is not null)
                        {
                            if (argumentsNode is JsonValue argumentValue
                                && argumentValue.TryGetValue<string>(out var argumentText))
                            {
                                builder.Arguments.Append(argumentText);
                            }
                            else
                            {
                                builder.Arguments.Append(argumentsNode.ToJsonString());
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(contentDelta) || !string.IsNullOrEmpty(reasoningDelta))
                {
                    stream?.Report(new AiStreamUpdate(contentDelta, reasoningDelta));
                }
            }

            var calls = toolCalls.Values
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .Select(b => new AiToolCall
                {
                    Id = string.IsNullOrWhiteSpace(b.Id) ? $"call_{Guid.NewGuid():N}" : b.Id,
                    Name = b.Name,
                    Arguments = b.Arguments.ToString(),
                })
                .ToList();

            // DSML 兜底：网关的 DSML→OpenAI 转换器偶发走偏，把 DSML 标记漏进正文。
            // 这里能还原的就还原成工具调用，还原不了的剥掉标记并标记 DsmlLeak（由调用方重试）。
            var rawContent = AiTextSanitizer.Clean(content.ToString());
            var dsml = AiDsmlParser.Parse(rawContent);

            if (dsml.Calls.Count > 0)
            {
                foreach (var parsed in dsml.Calls)
                {
                    calls.Add(new AiToolCall
                    {
                        Id = $"dsml_{Guid.NewGuid():N}",
                        Name = parsed.Name,
                        Arguments = parsed.ArgumentsJson,
                    });
                }
            }

            // 内部推理标记（<result>…</result>）有时会被网关写进可见正文，
            // 有时模型会陷入纯思考循环连吐几千个空标记。这里把它们摘出来归到「思考」区，
            // 正文里一个字都不留。
            var withoutDsml = dsml.HasDsml ? dsml.CleanedText : rawContent;
            var markup = AiInternalMarkup.Strip(withoutDsml);

            var finalReasoning = AiTextSanitizer.Clean(reasoning.ToString());

            if (!string.IsNullOrWhiteSpace(markup.InternalText))
            {
                finalReasoning = string.IsNullOrWhiteSpace(finalReasoning)
                    ? markup.InternalText
                    : finalReasoning + "\n\n" + markup.InternalText;
            }

            return new AttemptOutcome
            {
                Ok = true,
                Content = markup.CleanedText,
                Reasoning = finalReasoning,
                ToolCalls = calls,
                Usage = usage,
                FinishReason = finishReason,
                DsmlLeak = dsml.Unparsable,
                ReasoningOnly = markup.ReasoningOnly,
                MarkerLoop = markup.LooksLikeLoop,
            };
        }
    }

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    private static AiUsageTotals ParseUsage(JsonNode node)
    {
        long Get(string name)
        {
            try
            {
                return node[name]?.GetValue<long>() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        long reasoning = 0;
        try
        {
            reasoning = node["completion_tokens_details"]?["reasoning_tokens"]?.GetValue<long>() ?? 0;
        }
        catch
        {
            // 忽略
        }

        return new AiUsageTotals
        {
            PromptTokens = Get("prompt_tokens"),
            CompletionTokens = Get("completion_tokens"),
            ReasoningTokens = reasoning,
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";

    /// <summary>从 OpenAI 风格的错误体里取出可读的 error.message，取不到就截断原文。</summary>
    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "（无响应内容）";
        }

        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return Truncate(message, 120);
            }
        }
        catch
        {
            // 非 JSON，走截断
        }

        return Truncate(body, 120);
    }

    private static AiCredentials LoadCredentials()
    {
        // 凭据来源有两层：
        //   ① %AppData%\ClassManager\ai-credentials.json（用户自填，明文，优先级高）
        //   ② 程序内置的 AES 密文（credentials.enc，sk- 扫不出来）
        // 具体逻辑都在 AiCredentialVault 里，这里只管拿结果。
        var json = AiCredentialVault.ReadJson();

        return AiCredentialVault.Parse(json) ?? new AiCredentials();
    }
}
