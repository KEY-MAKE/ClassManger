using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClassManager.App.Services;

/// <summary>
/// DSML（DeepSeek Markup Language）兜底解析器。
/// <para>
/// 背景：DeepSeek V3.2/V4 的原生工具调用格式是 DSML：
/// <code>
/// &lt;｜DSML｜tool_calls&gt;
/// &lt;｜DSML｜invoke name="run_python"&gt;
/// &lt;｜DSML｜parameter name="code" string="true"&gt;print(1)&lt;/｜DSML｜parameter&gt;
/// &lt;/｜DSML｜invoke&gt;
/// &lt;/｜DSML｜tool_calls&gt;
/// </code>
/// 部分 OpenAI 兼容网关的 DSML→OpenAI 转换器会偶发走偏，把这段标记直接泄漏到可见的
/// <c>content</c> 里（而不是返回结构化 <c>tool_calls</c>）。本项目实测出现过两种坏形态：
/// ① 标记残缺（invoke 头被吞）；② 标签属性被误当成函数名（工具名变成 <c>description" string="true</c>）。
/// </para>
/// <para>
/// 这里做两件事：把能还原的 DSML 还原成标准工具调用并执行；把正文里的 DSML 标记剥掉，
/// 避免界面上出现「｜DSML｜」这种乱码。还原不出来时（形态 ①）交给调用方重试。
/// </para>
/// </summary>
public static class AiDsmlParser
{
    /// <summary>解析出来的一次工具调用。</summary>
    public sealed class ParsedCall
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>拼装好的 JSON 参数（OpenAI 规范要求 arguments 是 JSON 字符串）。</summary>
        public string ArgumentsJson { get; init; } = "{}";

        /// <summary>参数键值对。</summary>
        public Dictionary<string, object?> Parameters { get; init; } = new();
    }

    /// <summary>JSON 序列化选项：中文保持原样，不要转义成 \uXXXX。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>解析结果。</summary>
    public sealed class ParseResult
    {
        /// <summary>正文里是否出现过 DSML 标记。</summary>
        public bool HasDsml { get; init; }

        /// <summary>剥掉 DSML 之后的正文。</summary>
        public string CleanedText { get; init; } = string.Empty;

        /// <summary>还原出来的工具调用。</summary>
        public IReadOnlyList<ParsedCall> Calls { get; init; } = Array.Empty<ParsedCall>();

        /// <summary>出现了 DSML 标记，但一个完整调用都没还原出来。</summary>
        public bool Unparsable => HasDsml && Calls.Count == 0;
    }

    // 行首的 DSML 标记，且后面必须跟结构关键字（invoke / parameter / tool_calls …）。
    // 只判「行首」不够：用户问「｜DSML｜ 是什么」时答案往往以该词开头，
    // 那样会被误判成泄漏 → 回答被腰斩还白重试。
    private static readonly Regex LineStartDsmlRegex = new(
        @"^[ \t]*(?:<)?[|｜]DSML[|｜][ \t]*(?:invoke|parameter|tool_calls|calls|function_calls)\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // 网关泄漏出来的 DSML，开标签的 '<' 会被吞掉（真实样本：'｜DSML｜ parameter name="code" ...'），
    // 而闭标签的 '</' 还在。所以 '<' 与 '</' 都必须按可选处理。
    // 竖线要同时认全角 ｜(U+FF5C) 与 ASCII | 两种。

    private static readonly Regex InvokeRegex = new(
        @"(?:<)?[|｜]DSML[|｜]invoke\s+name\s*=\s*""([^""]+)""\s*>(.*?)(?:</)?[|｜]DSML[|｜]invoke\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex ParameterRegex = new(
        @"(?:<)?[|｜]DSML[|｜]parameter\s+name\s*=\s*""([^""]+)""\s+string\s*=\s*""(true|false)""\s*>(.*?)(?:</)?[|｜]DSML[|｜]parameter\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 完整的 DSML 块（整块删掉，含块里的正文）
    private static readonly Regex InvokeBlockRegex = new(
        @"(?:<)?[|｜]DSML[|｜]invoke\b.*?(?:</)?[|｜]DSML[|｜]invoke\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex EnvelopeBlockRegex = new(
        @"(?:<)?[|｜]DSML[|｜](?:tool_calls|calls|function_calls)\b.*?(?:</)?[|｜]DSML[|｜](?:tool_calls|calls|function_calls)\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 零散的单标签
    private static readonly Regex AnyDsmlTagRegex = new(
        @"(?:</)?[|｜]DSML[|｜][^>]*>",
        RegexOptions.IgnoreCase);

    /// <summary>解析正文中的 DSML。</summary>
    public static ParseResult Parse(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new ParseResult();
        }

        // 必须「行首」才算 DSML 泄漏：用户完全可能在正文里提到 ｜DSML｜ 这个词
        // （比如问「DSML 是什么」），那种情况不能被当成泄漏去截断重试。
        var hasDsml = LineStartDsmlRegex.IsMatch(content);

        if (!hasDsml)
        {
            return new ParseResult { CleanedText = content };
        }

        var calls = new List<ParsedCall>();

        foreach (Match invoke in InvokeRegex.Matches(content))
        {
            var name = invoke.Groups[1].Value.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            var writer = new StringBuilder();
            writer.Append('{');

            var first = true;

            foreach (Match parameter in ParameterRegex.Matches(invoke.Groups[2].Value))
            {
                var key = parameter.Groups[1].Value.Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                var raw = parameter.Groups[3].Value;

                object? value;
                if (parameter.Groups[2].Value.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    // string="false" 表示值是 JSON
                    try
                    {
                        value = JsonNode.Parse(raw);
                    }
                    catch
                    {
                        value = raw;
                    }
                }
                else
                {
                    // string="true" 表示值是原始字符串
                    value = raw;
                }

                parameters[key] = value;

                if (!first)
                {
                    writer.Append(',');
                }

                first = false;

                writer.Append(JsonSerializer.Serialize(key, JsonOptions));
                writer.Append(':');

                writer.Append(value is JsonNode node
                    ? node.ToJsonString(JsonOptions)
                    : JsonSerializer.Serialize(value, JsonOptions));
            }

            writer.Append('}');

            calls.Add(new ParsedCall
            {
                Name = name,
                ArgumentsJson = writer.ToString(),
                Parameters = parameters,
            });
        }

        // 剥掉正文里的 DSML 标记（含残缺的），不让界面出现「｜DSML｜」
        var cleaned = EnvelopeBlockRegex.Replace(content, string.Empty);
        cleaned = InvokeBlockRegex.Replace(cleaned, string.Empty);
        cleaned = AnyDsmlTagRegex.Replace(cleaned, string.Empty);

        if (calls.Count == 0)
        {
            // 一个完整调用都没还原出来 → 第一个标记之后的全部内容都是 DSML 垃圾。
            // 真实样本里那整段 Python 源码就夹在残缺标签中间，
            // 只删标签会把它留下来变成界面上的乱码，所以这里整段截掉。
            var markerIndex = content.IndexOf("｜DSML｜", StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                markerIndex = content.IndexOf("|DSML|", StringComparison.Ordinal);
            }

            cleaned = markerIndex >= 0 ? content[..markerIndex] : string.Empty;
        }

        cleaned = cleaned.Trim();

        return new ParseResult
        {
            HasDsml = true,
            CleanedText = cleaned,
            Calls = calls,
        };
    }
}
