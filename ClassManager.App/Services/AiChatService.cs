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
internal sealed class AiCredentials
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public List<string> Keys { get; init; } = new();
}

public sealed class AiChatService : IAiChatService
{
    private const string CredentialResourceSuffix = "ai.credentials.json";

    private readonly HttpClient _http;
    private readonly AiCredentials _credentials;
    private int _activeKeyIndex;

    public AiChatService()
    {
        _credentials = LoadCredentials();

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

        var body = BuildRequestBody(history, tools);

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

        var rateLimited = errors.Any(e => e.Contains("限流", StringComparison.Ordinal));

        return new AiTurnResult
        {
            Ok = false,
            Error = rateLimited
                ? "所有通道都触发了限流（每个通道已重试 3 次）。\n"
                  + "这是服务端的请求频率限制，通常等 30~60 秒就会恢复，稍后重试即可。\n"
                  + "详细：" + string.Join("；", errors.TakeLast(2))
                : "所有 API Key 均不可用。\n" + string.Join("\n", errors.TakeLast(3)),
        };
    }

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

                工作方式：
                1. 需要动手就调工具，不要只描述步骤。
                2. 输出文件前先把目录建好（create_directory），再写文件。
                3. 生成 Word/PPT 时用 Markdown：# 是标题，- 是列表条目。
                4. 工具返回「用户拒绝」时不要重试同一个操作，也不要直接结束任务；
                   要向用户说明你需要什么才能继续（换目录？让他点允许？），然后等用户回复。
                5. 最后用几句话说清你做了什么、产物在哪。
                """;
    }

    private string BuildRequestBody(IReadOnlyList<AiMessage> history, IReadOnlyList<AiToolSpec> tools)
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
            ["model"] = _credentials.Model,
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
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(CredentialResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            return new AiCredentials();
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return new AiCredentials();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();

        try
        {
            return JsonSerializer.Deserialize<AiCredentials>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new AiCredentials();
        }
        catch
        {
            return new AiCredentials();
        }
    }
}
