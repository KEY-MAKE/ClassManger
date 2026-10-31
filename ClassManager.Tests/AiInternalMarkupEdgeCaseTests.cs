using System;
using System.Linq;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 内部标记剥离器的<b>边界行为</b>。
/// <para>
/// 重点验证「剥离逻辑有没有误伤正常正文」：
/// 正文里不成标签的 '&lt;result'、单独闭合标签 '&lt;/result>'、
/// 用户真的想让 AI 解释 '&lt;result&gt;' 这几个字、以及标记之前/之后的内容归属。
/// </para>
/// <para>
/// 标注「现状断言」的用例记录的是<b>当前真实行为</b>（含缺陷），
/// 修复后应把断言改成期望值。这样缺陷不会被静默改掉。
/// </para>
/// </summary>
public class AiInternalMarkupEdgeCaseTests
{
    // ------------------------------------------------------------ 正文不被误伤

    [Fact]
    public void Non_tag_angle_bracket_text_is_preserved()
    {
        // 正文里正常出现 "<result"（不成标签）
        const string content = "请看这段 <result 和 <resultx 以及 <results 的文本。";

        var result = AiInternalMarkup.Strip(content);

        // 内容一个字都没丢
        Assert.Equal(content, result.CleanedText);
        Assert.DoesNotContain("思考", result.InternalText, StringComparison.Ordinal);
        Assert.False(result.ReasoningOnly);
    }

    [Fact]
    public void Normal_text_with_angle_brackets_is_not_a_loop()
    {
        var result = AiInternalMarkup.Strip("当 x < 1 且 y < 2 时，z < 3 成立。");

        Assert.False(result.LooksLikeLoop);
        Assert.Contains("z < 3", result.CleanedText, StringComparison.Ordinal);
    }
    /// <summary>修复后：20 个 '&lt;' 的合法代码不再被误判成空转循环。</summary>

    [Fact]
    public void Normal_code_answer_with_many_less_than_signs_is_not_a_loop()
    {
        var code = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"if (a{i} < b{i}) {{ }}"));

        var result = AiInternalMarkup.Strip(code);

        // 修复后：内部标记数是 0（不是 '<' 的总数 20），不会被误判成空转循环
        Assert.Equal(0, result.MarkerCount);
        Assert.False(result.LooksLikeLoop);
        Assert.Equal(code, result.CleanedText);
    }
    /// <summary>修复后：正常 XML 回答不再被误判成空转循环。</summary>

    [Fact]
    public void Xml_answer_with_many_tags_is_not_a_loop()
    {
        var xml = string.Concat(Enumerable.Range(0, 12).Select(i => $"<item id=\"{i}\">值</item>"));

        var result = AiInternalMarkup.Strip(xml);

        // 修复后：24 个 '<' 的正常 XML 不再被误判成循环
        Assert.False(result.LooksLikeLoop);
        Assert.Equal(0, result.MarkerCount);
        Assert.Equal(xml, result.CleanedText);
    }

    // ------------------------------------------------------------ 标记归属

    [Fact]
    public void Body_before_the_marker_is_kept_verbatim()
    {
        const string content = "这是给用户的结论。\n\n<result>\n这是内部思考，不该给用户看。\n";

        var result = AiInternalMarkup.Strip(content);

        Assert.Equal("这是给用户的结论。", result.CleanedText);
        Assert.Contains("内部思考", result.InternalText, StringComparison.Ordinal);
        Assert.False(result.ReasoningOnly);
    }

    [Fact]
    public void Body_between_two_markers_is_kept()
    {
        var result = AiInternalMarkup.Strip("A\n<result>思考1</result>\nB\n<result>思考2</result>\nC");

        Assert.Contains("A", result.CleanedText, StringComparison.Ordinal);
        Assert.Contains("B", result.CleanedText, StringComparison.Ordinal);
        Assert.Contains("C", result.CleanedText, StringComparison.Ordinal);
        Assert.DoesNotContain("思考1", result.CleanedText, StringComparison.Ordinal);
        Assert.DoesNotContain("思考2", result.CleanedText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 单独闭合标签

    [Fact]
    public void Lone_closing_tag_is_currently_left_in_the_body()
    {
        // 现状断言：单独的 </result> 既不成对也不匹配开标记，
        // 于是原样留在用户可见正文里（与开标签的处理不对称）
        const string content = "闭合写法是 </result> 这样。";

        var result = AiInternalMarkup.Strip(content);

        Assert.False(result.HasInternal);
        Assert.Equal(content, result.CleanedText);
        Assert.Contains("</result>", result.CleanedText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 用户真想看到 <result> 字样

    [Fact]
    public void User_asking_about_result_tag_currently_gets_the_answer_mangled()
    {
        // 用户问「<result> 是什么」，模型照实解释
        const string content = "<result> 是 DeepSeek 用来包裹内部思考的标签，</result> 是它的闭合形式。";

        var result = AiInternalMarkup.Strip(content);

        // 现状断言（缺陷 P1-A）：正文被腰斩，前半句被静默挪进「思考」区
        Assert.Equal("是它的闭合形式。", result.CleanedText);
        Assert.Contains("DeepSeek 用来包裹内部思考的标签", result.InternalText, StringComparison.Ordinal);

        // 而且不会触发重试（ReasoningOnly=false），用户直接看到一个残缺答案
        Assert.False(result.ReasoningOnly);
        Assert.False(result.LooksLikeLoop);
    }

    // ------------------------------------------------------------ 嵌套 / 畸形

    [Fact]
    public void Nested_markers_currently_leak_a_closing_tag_into_the_body()
    {
        // 现状断言（缺陷 P1-A）：嵌套标记下正则只吃掉最外层的一部分，
        // 残留的 "</result>" 出现在用户可见正文里
        var result = AiInternalMarkup.Strip("<result>外层<result>内层</result>尾巴</result>结尾");

        Assert.Contains("</result>", result.CleanedText, StringComparison.Ordinal);
        Assert.Contains("结尾", result.CleanedText, StringComparison.Ordinal);
    }
    /// <summary>
    /// 保守策略：未闭合标记只有在「行首且独占一行」时才判定为内部内容。
    /// 这样用户让 AI 解释 '&lt;result&gt;' 这个标签时，回答不会被腰斩。
    /// </summary>

    [Fact]
    public void Unclosed_marker_takes_everything_after_it()
    {
        // 保守策略：只有「行首且独占一行」的标记才判定为内部内容。
        // 真实泄漏样本（&lt;result&gt;\n思考…）是独占一行，能抓到；
        // 而 '&lt;result&gt;abc' 这种同行写法可能是用户让 AI 解释这个标签，保守保留正文。
        var result = AiInternalMarkup.Strip("<result>abc");

        Assert.Equal("<result>abc", result.CleanedText);
        Assert.False(result.ReasoningOnly);
    }

    // ------------------------------------------------------------ 空回复

    [Fact]
    public void Empty_reply_is_not_flagged_as_reasoning_only()
    {
        // 现状断言：ReasoningOnly 要求「有标记」，所以既无标记又无正文的空回复
        // 不会被任何异常标志捕获 → ViewModel 会按「正常完成」处理
        foreach (var content in new[] { "", "   \n\t ", "```\n```" })
        {
            var result = AiInternalMarkup.Strip(content);

            Assert.False(result.HasInternal);
            Assert.False(result.ReasoningOnly);
            Assert.False(result.LooksLikeLoop);
        }
    }

    // ------------------------------------------------------------ 计数口径

    [Fact]
    public void Marker_count_counts_angle_brackets_not_internal_markers()
    {
        // 现状断言（缺陷 P0-A）：10 个 <result>x</result> 有 20 个 '<'，
        // 减去 10 个闭合标签后凑巧等于 10 —— 属于歪打正着
        var paired = string.Concat(Enumerable.Repeat("<result>x</result>", 10));

        var result = AiInternalMarkup.Strip(paired);

        Assert.Equal(10, result.MarkerCount);

        // 但 10 个开标记（无闭合）会被算成 10，20 个开标记算成 20
        var unclosed = string.Concat(Enumerable.Repeat("<result>", 20));
        Assert.Equal(20, AiInternalMarkup.Strip(unclosed).MarkerCount);
    }

    [Fact]
    public void Real_loop_sample_is_detected()
    {
        // 真实事故：28213 字、3098 个 <result>、0 个闭合标签
        var sample = string.Concat(Enumerable.Repeat("<result>\n", 3098));

        var result = AiInternalMarkup.Strip(sample);

        Assert.True(result.LooksLikeLoop);
        Assert.True(result.ReasoningOnly);
        Assert.Equal(string.Empty, result.CleanedText);
    }
}
