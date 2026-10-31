using System;
using System.Linq;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// DSML 兜底解析器的契约。
/// <para>
/// 背景：网关的 DSML→OpenAI 转换器偶发走偏，把 DeepSeek 原生工具调用标记漏进可见正文。
/// 客户端必须能把能还原的还原成工具调用、把还原不了的标记剥掉并交给上层重试，
/// 绝不能让用户看到「｜DSML｜」这种乱码，更不能因此静默结束任务。
/// </para>
/// </summary>
public class AiDsmlParserTests
{
    /// <summary>真实事故样本：消息 [14]，invoke 头被网关吞掉了。</summary>
    private const string RealTruncatedSample =
        "｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>";

    /// <summary>真实事故样本：消息 [22]，带完整 parameter 但 invoke 头同样被吞。</summary>
    private const string RealTruncatedWithCode =
        "｜DSML｜ parameter name=\"code\" string=\"true\">\nimport zipfile, re, os, sys\n" +
        "sys.stdout.reconfigure(encoding='utf-8')\n" +
        "</｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>";

    [Fact]
    public void Normal_text_is_untouched()
    {
        var result = AiDsmlParser.Parse("这是一句普通的话。");

        Assert.False(result.HasDsml);
        Assert.False(result.Unparsable);
        Assert.Empty(result.Calls);
        Assert.Equal("这是一句普通的话。", result.CleanedText);
    }

    [Fact]
    public void Null_and_empty_are_safe()
    {
        Assert.False(AiDsmlParser.Parse(null).HasDsml);
        Assert.False(AiDsmlParser.Parse(string.Empty).HasDsml);
    }

    [Fact]
    public void Real_truncated_sample_is_detected_and_stripped()
    {
        var result = AiDsmlParser.Parse(RealTruncatedSample);

        // 识别出这是 DSML 泄漏
        Assert.True(result.HasDsml);

        // invoke 头被吞了，还原不出工具名 → 交给上层重试
        Assert.Empty(result.Calls);
        Assert.True(result.Unparsable);

        // 界面上不能出现「｜DSML｜」
        Assert.DoesNotContain("DSML", result.CleanedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_truncated_sample_with_code_is_stripped_and_not_executed()
    {
        var result = AiDsmlParser.Parse(RealTruncatedWithCode);

        Assert.True(result.HasDsml);
        Assert.Empty(result.Calls);
        Assert.True(result.Unparsable);
        Assert.DoesNotContain("DSML", result.CleanedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("import zipfile", result.CleanedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_width_pipe_variant_is_parsed_back_to_tool_call()
    {
        var dsml =
            "｜DSML｜tool_calls>\n" +
            "｜DSML｜invoke name=\"run_python\">\n" +
            "｜DSML｜parameter name=\"code\" string=\"true\">print(1)\n" +
            "</｜DSML｜parameter>\n" +
            "</｜DSML｜invoke>\n" +
            "</｜DSML｜calls>";

        var result = AiDsmlParser.Parse(dsml);

        Assert.True(result.HasDsml);
        Assert.False(result.Unparsable);
        var call = Assert.Single(result.Calls);
        Assert.Equal("run_python", call.Name);
        Assert.Contains("print(1)", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("DSML", result.CleanedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ascii_pipe_variant_is_parsed_back_to_tool_call()
    {
        var dsml =
            "<|DSML|tool_calls>\n" +
            "<|DSML|invoke name=\"read_file\">\n" +
            "<|DSML|parameter name=\"path\" string=\"true\">a.txt</|DSML|parameter>\n" +
            "</|DSML|invoke>\n" +
            "</|DSML|tool_calls>";

        var result = AiDsmlParser.Parse(dsml);

        var call = Assert.Single(result.Calls);
        Assert.Equal("read_file", call.Name);
        Assert.Contains("a.txt", call.ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_valued_parameter_is_parsed_as_json()
    {
        var dsml =
            "<｜DSML｜invoke name=\"write_xlsx\">\n" +
            "<｜DSML｜parameter name=\"sheets\" string=\"false\">{\"成绩\":[{\"姓名\":\"张三\"}]}</｜DSML｜parameter>\n" +
            "<｜DSML｜parameter name=\"path\" string=\"true\">a.xlsx</｜DSML｜parameter>\n" +
            "</｜DSML｜invoke>";

        var result = AiDsmlParser.Parse(dsml);

        var call = Assert.Single(result.Calls);
        Assert.Equal("write_xlsx", call.Name);
        Assert.Contains("张三", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.Contains("a.xlsx", call.ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_invocations_are_all_recovered()
    {
        var dsml =
            "<｜DSML｜invoke name=\"list_files\"><｜DSML｜parameter name=\"path\" string=\"true\"></｜DSML｜parameter></｜DSML｜invoke>\n" +
            "<｜DSML｜invoke name=\"read_file\"><｜DSML｜parameter name=\"path\" string=\"true\">x</｜DSML｜parameter></｜DSML｜invoke>";

        var result = AiDsmlParser.Parse(dsml);

        Assert.Equal(2, result.Calls.Count);
        Assert.Equal("list_files", result.Calls[0].Name);
        Assert.Equal("read_file", result.Calls[1].Name);
    }

    [Fact]
    public void Text_mixed_with_dsml_keeps_the_text()
    {
        var dsml =
            "我先看看目录。\n" +
            "<｜DSML｜invoke name=\"list_files\"><｜DSML｜parameter name=\"path\" string=\"true\"></｜DSML｜parameter></｜DSML｜invoke>";

        var result = AiDsmlParser.Parse(dsml);

        Assert.Single(result.Calls);
        Assert.Contains("我先看看目录。", result.CleanedText, StringComparison.Ordinal);
        Assert.DoesNotContain("DSML", result.CleanedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_tool_declares_its_parameters()
    {
        // 模型看不到参数名就只能从中文散文里猜——这是 DSML 畸形输出的诱因之一
        foreach (var spec in AiFileTools.Specs)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(spec.ParametersJson),
                $"{spec.Name} 缺少参数 schema");

            Assert.Contains("\"properties\"", spec.ParametersJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"properties\":{}", spec.ParametersJson, StringComparison.Ordinal);
            Assert.Contains("\"required\"", spec.ParametersJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tool_parameter_names_match_the_code_that_reads_them()
    {
        // 每个工具声明的必填参数，必须真的被代码读取，否则模型传对了也会被忽略
        var expected = new System.Collections.Generic.Dictionary<string, string[]>
        {
            ["list_files"] = new[] { "path" },
            ["read_file"] = new[] { "path" },
            ["write_file"] = new[] { "path", "content" },
            ["write_xlsx"] = new[] { "path", "sheets" },
            ["write_docx"] = new[] { "path", "content" },
            ["write_pptx"] = new[] { "path", "content" },
            ["search_tool"] = new[] { "query" },
            ["install_tool"] = new[] { "package" },
            ["run_python"] = new[] { "code" },
            ["run_node"] = new[] { "code" },
            ["web_fetch"] = new[] { "url" },
            ["create_directory"] = new[] { "path" },
        };

        foreach (var spec in AiFileTools.Specs)
        {
            Assert.True(expected.ContainsKey(spec.Name), $"未登记的工具：{spec.Name}");

            foreach (var parameter in expected[spec.Name])
            {
                Assert.Contains(
                    $"\"{parameter}\"",
                    spec.ParametersJson,
                    StringComparison.Ordinal);
            }
        }
    }
}
