using System;
using System.Linq;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 独立验收发现的问题的回归测试。
/// 每一条都对应一个真实缺陷，修完必须钉死。
/// </summary>
public class AiMarkupRegressionTests
{
    /// <summary>
    /// P0：标记计数原本数的是「'&lt;' 的个数」，一段含 20 个 '&lt;' 的正常代码/XML
    /// 回答会被误判成空转循环 → 白重试 2 次 → 最后报「模型陷入循环」这种完全错误的错。
    /// </summary>
    [Fact]
    public void Many_angle_brackets_in_normal_answer_are_not_a_loop()
    {
        var code = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"<div class=\"row-{i}\">内容</div>"));
        var answer = "这是正常的代码示例：\n" + code + "\n结束。";

        var result = AiInternalMarkup.Strip(answer);

        Assert.False(result.LooksLikeLoop, "含很多 '<' 的正常回答不能被判成空转循环");
        Assert.False(result.ReasoningOnly);
        Assert.Equal(0, result.MarkerCount);
        Assert.Contains("结束。", result.CleanedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// P1：用户问「&lt;result&gt; 是什么」时，回答会被腰斩且不报错。
    /// 内部标记必须「行首」才算，正文里提到的字样要原样保留。
    /// </summary>
    [Fact]
    public void Mentioning_result_in_prose_is_not_stripped()
    {
        var answer = "你问的 <result> 是 DeepSeek 的一种内部标记，正常回答里出现它没关系。";

        var result = AiInternalMarkup.Strip(answer);

        Assert.False(result.HasInternal);
        Assert.Equal(answer, result.CleanedText);
        Assert.Contains("<result>", result.CleanedText, StringComparison.Ordinal);
    }

    /// <summary>P1：正文里提到 ｜DSML｜ 不该被当成「DSML 泄漏」去截断重试。</summary>
    [Fact]
    public void Mentioning_dsml_in_prose_is_not_a_leak()
    {
        var answer = "｜DSML｜ 是 DeepSeek 的工具调用格式，你也可以在别处看到它。";

        var result = AiDsmlParser.Parse(answer);

        Assert.False(result.HasDsml);
        Assert.False(result.Unparsable);
        Assert.Equal(answer, result.CleanedText);
    }

    /// <summary>P1：纠错提示以「用户」身份落盘，恢复历史时必须被过滤掉。</summary>
    [Fact]
    public void System_correction_messages_are_recognised()
    {
        Assert.True(AiInternalMarkup.IsSystemCorrection(
            AiInternalMarkup.SystemCorrectionMarker + "上一轮你只输出了内部思考内容…"));

        Assert.False(AiInternalMarkup.IsSystemCorrection("帮我改一下这个文件"));
        Assert.False(AiInternalMarkup.IsSystemCorrection(null));
    }

    /// <summary>行首的内部标记仍然要能被识别（别把修复做过头）。</summary>
    [Fact]
    public void Line_start_marker_is_still_detected()
    {
        var content = "<result>\n这是内部思考。\n";

        var result = AiInternalMarkup.Strip(content);

        Assert.True(result.HasInternal);
        Assert.Equal(string.Empty, result.CleanedText);
        Assert.True(result.ReasoningOnly);
    }

    /// <summary>代码围栏 + 内部标记（真实样本形态）：整段算内部。</summary>
    [Fact]
    public void Fence_plus_marker_is_treated_as_internal()
    {
        var content = "```text\n开始自检。\n\n<result>\n自检结果如下…\n";

        var result = AiInternalMarkup.Strip(content);

        Assert.True(result.HasInternal);
        Assert.Equal(string.Empty, result.CleanedText);
    }
}
