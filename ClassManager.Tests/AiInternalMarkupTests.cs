using System;
using System.Linq;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 内部推理标记剥离器的契约。
/// <para>
/// 实测事故：网关把模型的思考写进了可见 content，并用 &lt;result&gt; 包裹；
/// 模型陷入纯思考循环时更是连吐 3098 个空 &lt;result&gt; 标记，
/// 界面上看起来就像「一直循环返回 &lt;result&gt;」。
/// </para>
/// </summary>
public class AiInternalMarkupTests
{
    /// <summary>真实样本：消息 [47]，整条正文只有一个 result 块。</summary>
    private const string RealSample =
        "<result>\n我拿到了全部官方答案文本。现在按规范把它们组装成答案卷：`【答案】N.`；主观题的续行不再写题号。\n\n" +
        "需要注意 18、19 题带（1）（2）分隔，规范允许这样写。\n\n先提取完整的答案原文，并确保文本无截断。\n\n\n\n\n\n";

    /// <summary>真实样本：消息 [58]，3098 个空 result 标记的空转循环。</summary>
    private static string BuildLoopSample()
    {
        var head = "<result>\n答案区题号检测漏了第 1 题——因为它写在与【答案】同一行。\n\n" +
                   "<result>\n现在验证一下：改成 【答案】 独占一行。\n\n" +
                   "<result>\n同时我会检查两卷的完整最终状态。\n\n";

        return head + string.Concat(Enumerable.Repeat("<result>\n", 3000));
    }

    [Fact]
    public void Normal_text_is_untouched()
    {
        var result = AiInternalMarkup.Strip("这是给用户的正常回答。");

        Assert.False(result.HasInternal);
        Assert.Equal("这是给用户的正常回答。", result.CleanedText);
        Assert.Equal(0, result.MarkerCount);
        Assert.False(result.ReasoningOnly);
    }

    [Fact]
    public void Real_sample_is_moved_out_of_the_body()
    {
        var result = AiInternalMarkup.Strip(RealSample);

        // 识别出内部标记
        Assert.True(result.HasInternal);
        Assert.Equal(1, result.MarkerCount);

        // 正文里一个字都不留
        Assert.Equal(string.Empty, result.CleanedText);
        Assert.DoesNotContain("result", result.CleanedText, StringComparison.OrdinalIgnoreCase);

        // 内容归到思考区
        Assert.Contains("我拿到了全部官方答案文本", result.InternalText, StringComparison.Ordinal);
        Assert.True(result.ReasoningOnly);
    }

    [Fact]
    public void Loop_sample_is_detected_and_emptied()
    {
        var result = AiInternalMarkup.Strip(BuildLoopSample());

        // 标记数量异常 → 判定为空转循环
        Assert.True(result.LooksLikeLoop);
        Assert.True(result.MarkerCount >= AiInternalMarkup.LoopMarkerThreshold);

        // 正文为空（不会把 3000 个标记甩到界面上）
        Assert.Equal(string.Empty, result.CleanedText);
        Assert.True(result.ReasoningOnly);
    }

    [Fact]
    public void Body_before_internal_markup_is_kept()
    {
        var content = "这是给用户的结论。\n\n<result>\n这是内部思考，不该给用户看。\n";

        var result = AiInternalMarkup.Strip(content);

        Assert.Equal("这是给用户的结论。", result.CleanedText);
        Assert.Contains("内部思考", result.InternalText, StringComparison.Ordinal);
        Assert.False(result.ReasoningOnly);
    }

    [Fact]
    public void Closed_block_is_extracted()
    {
        var content = "回答正文。\n<result>思考内容</result>\n更多正文。";

        var result = AiInternalMarkup.Strip(content);

        Assert.Contains("思考内容", result.InternalText, StringComparison.Ordinal);
        Assert.DoesNotContain("思考内容", result.CleanedText, StringComparison.Ordinal);
        Assert.Contains("回答正文。", result.CleanedText, StringComparison.Ordinal);
        Assert.Contains("更多正文。", result.CleanedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_fence_markers_are_kept()
    {
        // 用户可能真的要看代码块 → 围栏要保留，不能一律剥掉
        var result = AiInternalMarkup.Strip("```text\n普通内容\n```");

        Assert.Contains("```", result.CleanedText, StringComparison.Ordinal);
        Assert.Contains("普通内容", result.CleanedText, StringComparison.Ordinal);
        Assert.False(result.HasInternal);
    }

    [Fact]
    public void Null_and_empty_are_safe()
    {
        Assert.False(AiInternalMarkup.Strip(null).HasInternal);
        Assert.False(AiInternalMarkup.Strip(string.Empty).HasInternal);
    }

    [Fact]
    public void A_single_mark_is_not_treated_as_loop()
    {
        // 正常的一轮思考只有一个 result 块，不应被判成循环
        var result = AiInternalMarkup.Strip(RealSample);

        Assert.False(result.LooksLikeLoop);
    }
}
