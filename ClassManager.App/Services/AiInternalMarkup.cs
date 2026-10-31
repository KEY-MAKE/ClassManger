using System.Text.RegularExpressions;

namespace ClassManager.App.Services;

/// <summary>
/// 内部标记剥离器：把模型/网关塞在正文里的「内部推理标记」摘出来。
/// <para>
/// 实测现象（用户真实会话）：网关有时不把思考放进 <c>reasoning_content</c>，
/// 而是直接写进可见的 <c>content</c>，并用 <c>&lt;result&gt;</c> 包裹每一段思考：
/// </para>
/// <code>
/// &lt;result&gt;
/// 我拿到了全部官方答案文本。现在按规范把它们组装成答案卷…
/// &lt;result&gt;
/// 现在验证一下：改成 【答案】 独占一行…
/// </code>
/// <para>
/// 更糟的情况是模型陷入纯思考循环，于是连吐几千个空的 <c>&lt;result&gt;</c>，
/// 界面上看起来就像「一直循环返回 &lt;result&gt;」。
/// </para>
/// <para>
/// <b>判定必须「行首」</b>：真实样本里内部标记总是独占一行或出现在开头；
/// 而用户完全可能在正文里提到 &lt;result&gt; 这个词（例如问「&lt;result&gt; 是什么」），
/// 那种情况不能被腰斩。按行首判定，两种情况就能区分开。
/// </para>
/// </summary>
public static class AiInternalMarkup
{
    /// <summary>标记数量超过这个数就认为模型在空转循环。</summary>
    public const int LoopMarkerThreshold = 20;

    /// <summary>
    /// 系统内部纠错提示的前缀。这些提示是以「用户」身份发给模型让它换种方式的，
    /// 但它们不是用户说的话——恢复历史时必须过滤掉，否则会永久冒充用户发言、污染上下文。
    /// </summary>
    public const string SystemCorrectionMarker = "[系统内部纠错] ";

    /// <summary>判断一条消息是不是系统内部纠错提示（恢复历史时应丢弃）。</summary>
    public static bool IsSystemCorrection(string? content) =>
        content is not null && content.StartsWith(SystemCorrectionMarker, StringComparison.Ordinal);

    /// <summary>剥离结果。</summary>
    public sealed class Result
    {
        /// <summary>正文里是否出现过内部标记。</summary>
        public bool HasInternal { get; init; }

        /// <summary>剥离后的正文（用户该看到的）。</summary>
        public string CleanedText { get; init; } = string.Empty;

        /// <summary>摘出来的内部推理内容。</summary>
        public string InternalText { get; init; } = string.Empty;

        /// <summary>内部标记的数量（用来判断是否在空转循环）。</summary>
        public int MarkerCount { get; init; }

        /// <summary>是否只有内部推理、没有正文（模型没给出可读回答）。</summary>
        public bool ReasoningOnly { get; init; }

        /// <summary>是否像是在空转循环（标记过多）。</summary>
        public bool LooksLikeLoop => MarkerCount >= LoopMarkerThreshold;
    }

    /// <summary>
    /// 行首的内部标记，且必须「独占一行」（标记后到行尾只有空白）。
    /// 真实泄漏样本里 '&lt;result&gt;' 总是自己占一行；而用户问「&lt;result&gt; 是什么」时，
    /// 答案通常写成「&lt;result&gt; 是 DeepSeek 的…」，标记后面还有正文 —— 那种不能剥离。
    /// </summary>
    private static readonly Regex LineStartMarkerRegex = new(
        @"^[ \t]*<(result|thinking|reasoning|thought)>[ \t]*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>成对的内部标记块（必须从行首开始才算）。</summary>
    private static readonly Regex ClosedBlockRegex = new(
        @"^[ \t]*<(result|thinking|reasoning|thought)>.*?</\1>",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>任何位置的内部标记（仅用于统计数量）。</summary>
    private static readonly Regex AnyMarkerRegex = new(
        @"<(result|thinking|reasoning|thought)>",
        RegexOptions.IgnoreCase);

    /// <summary>只去掉标记本身，保留里面的内容。</summary>
    private static readonly Regex TagOnlyRegex = new(
        @"</?(result|thinking|reasoning|thought)>",
        RegexOptions.IgnoreCase);

    /// <summary>正文以代码围栏开头（真实样本里围栏 + 内部标记是一起出现的）。</summary>
    private static readonly Regex StartsWithFenceRegex = new(
        @"^[ \t]*```",
        RegexOptions.IgnoreCase);

    /// <summary>剥离正文里的内部标记。</summary>
    public static Result Strip(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new Result();
        }

        // 只统计真正的内部标记 —— 不能用「'<' 的个数」，
        // 否则一段含很多 < 的正常代码/XML 回答会被误判成空转循环。
        var markerCount = AnyMarkerRegex.Matches(content).Count;

        if (markerCount == 0)
        {
            return new Result { CleanedText = content.Trim() };
        }

        var internalParts = new List<string>();
        var working = content;

        var lineStart = LineStartMarkerRegex.Match(working);

        if (lineStart.Success && lineStart.Index <= 100)
        {
            // 开头就是内部内容 → 到结尾全是内部（真实样本与空转循环都是这个形态）
            if (StartsWithFenceRegex.IsMatch(working))
            {
                // 围栏 + 内部标记（真实样本 [56]）：整段算内部
                internalParts.Add(working);
                working = string.Empty;
            }
            else
            {
                internalParts.Add(working[lineStart.Index..]);
                working = working[..lineStart.Index];
            }
        }
        else
        {
            // 行首没有标记：只摘掉成对的块，正文里被提到的 <result> 保持原样
            working = ClosedBlockRegex.Replace(working, match =>
            {
                internalParts.Add(match.Value);
                return string.Empty;
            });
        }

        var cleaned = working.Trim();
        var internalText = string.Join("\n\n", internalParts.Select(CleanInternal)).Trim();

        return new Result
        {
            HasInternal = internalParts.Count > 0,
            CleanedText = cleaned,
            InternalText = internalText,
            MarkerCount = markerCount,
            ReasoningOnly = internalParts.Count > 0 && cleaned.Length == 0,
        };
    }

    private static string CleanInternal(string block) =>
        TagOnlyRegex.Replace(block, string.Empty).Trim();
}
