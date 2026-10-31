using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// Agent 工具集验收：能写各类文本文件、能生成 Excel / Word / PPT、能建目录、能联网。
/// 重点验证「生成出来的 Office 文件确实有效，且能被本程序自己读回来」。
/// </summary>
public class AiAgentToolsTests : IDisposable
{
    private readonly string _workspace;

    public AiAgentToolsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"cm_agent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);

        // 初始化服务（只读设置，无副作用），让内置引擎可用
        AppServices.Initialize();
    }

    private static string EngineRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassManager", "engine", "exam2ppt-1.0.0");

    private static AiFileTools CreateTools()
    {
        var settings = new AiSettings
        {
            PermissionMode = AiPermissionMode.FullDisk,
        };

        return new AiFileTools(settings, _ => Task.FromResult(true));
    }

    [Fact]
    public async Task Write_xlsx_creates_readable_workbook()
    {
        var tools = CreateTools();
        var path = Path.Combine(_workspace, "成绩表.xlsx");

        var result = await tools.ExecuteAsync(
            "write_xlsx",
            "{\"path\":\"" + path.Replace("\\", "\\\\") + "\",\"sheets\":{\"成绩\":[{\"姓名\":\"张三\",\"分数\":92},{\"姓名\":\"李四\",\"分数\":78}]}}",
            CancellationToken.None);

        Assert.True(File.Exists(path), "应生成 xlsx 文件：" + result);

        // 用读取器读回来，确认内容正确
        var read = AiDocumentReader.Read(path);
        Assert.Equal(AiDocumentReader.Kind.Spreadsheet, read.Kind);
        Assert.Contains("张三", read.Text, StringComparison.Ordinal);
        Assert.Contains("李四", read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_directory_works()
    {
        var tools = CreateTools();
        var dir = Path.Combine(_workspace, "子目录", "更深一层");

        var result = await tools.ExecuteAsync(
            "create_directory",
            "{\"path\":\"" + dir.Replace("\\", "\\\\") + "\"}",
            CancellationToken.None);

        Assert.True(Directory.Exists(dir), "应创建出目录：" + result);
    }

    [Fact]
    public async Task Write_text_file_creates_file()
    {
        var tools = CreateTools();
        var path = Path.Combine(_workspace, "笔记.md");

        var result = await tools.ExecuteAsync(
            "write_file",
            "{\"path\":\"" + path.Replace("\\", "\\\\") + "\",\"content\":\"# 标题\\n正文内容\\n\"}",
            CancellationToken.None);

        Assert.True(File.Exists(path), "应生成文本文件：" + result);
        Assert.Contains("正文内容", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_docx_creates_valid_word_file()
    {
        if (!File.Exists(Path.Combine(EngineRoot, "runtime", "python.exe")))
        {
            return; // 引擎未解压则跳过
        }

        var tools = CreateTools();
        var path = Path.Combine(_workspace, "报告.docx");
        var markdown = "# 学生分析报告\n\n## 结论\n\n- 平均分 85\n- 最高分 98\n";

        var result = await tools.ExecuteAsync(
            "write_docx",
            "{\"path\":\"" + path.Replace("\\", "\\\\") + "\",\"content\":\"" + markdown.Replace("\n", "\\n").Replace("\"", "\\\"") + "\"}",
            CancellationToken.None);

        Assert.True(File.Exists(path), "应生成 docx：" + result);

        // 是合法的 OOXML 包
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("word/document.xml"));
        }

        // 能被本程序读回来，且是干净文字
        var read = AiDocumentReader.Read(path);
        Assert.Equal(AiDocumentReader.Kind.Document, read.Kind);
        Assert.Contains("学生分析报告", read.Text, StringComparison.Ordinal);
        Assert.Contains("平均分", read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_pptx_creates_valid_slides()
    {
        if (!File.Exists(Path.Combine(EngineRoot, "runtime", "python.exe")))
        {
            return;
        }

        var tools = CreateTools();
        var path = Path.Combine(_workspace, "演示.pptx");
        var markdown = "# 第一页\n- 要点一\n- 要点二\n\n# 第二页\n- 要点三\n";

        var result = await tools.ExecuteAsync(
            "write_pptx",
            "{\"path\":\"" + path.Replace("\\", "\\\\") + "\",\"content\":\"" + markdown.Replace("\n", "\\n") + "\"}",
            CancellationToken.None);

        Assert.True(File.Exists(path), "应生成 pptx：" + result);

        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, slides);
    }

    [Fact]
    public async Task Web_fetch_returns_text_or_clear_error()
    {
        var tools = CreateTools();

        // 空 url 必须给出明确提示，而不是崩
        var empty = await tools.ExecuteAsync("web_fetch", "{}", CancellationToken.None);
        Assert.Contains("url", empty, StringComparison.OrdinalIgnoreCase);

        // 非法协议也要拦
        var bad = await tools.ExecuteAsync("web_fetch", "{\"url\":\"file:///C:/Windows/win.ini\"}", CancellationToken.None);
        Assert.Contains("http", bad, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, true);
        }
        catch
        {
            // 忽略
        }
    }
}
