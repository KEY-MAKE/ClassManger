using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 附件读取契约：交给模型的内容**永远只有文本**。
/// 之前 docx 被当二进制读，模型看到满屏乱码后直接放弃任务。
/// 这里覆盖文本 / 表格 / 文档 / 图片 / 二进制 五类，并断言任何一类都不会漏出字节流。
/// </summary>
public class AiAttachmentReaderTests : IDisposable
{
    private readonly string _dir;

    public AiAttachmentReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cm_reader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>核心保证：任何读取结果都不能含控制字符/替换符（即不能是原始字节）。</summary>
    private static void AssertNoBinary(string text)
    {
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Equal(
            0,
            text.Count(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'));
    }

    [Fact]
    public void Text_file_is_read_as_text()
    {
        var path = Path.Combine(_dir, "笔记.md");
        File.WriteAllText(path, "# 标题\n这是正文内容。\n");

        var result = AiDocumentReader.Read(path);

        Assert.Equal(AiDocumentReader.Kind.Text, result.Kind);
        Assert.True(result.Ok);
        Assert.Contains("这是正文内容", result.Text, StringComparison.Ordinal);
        AssertNoBinary(result.Text);
    }

    [Fact]
    public void Spreadsheet_is_rendered_as_text()
    {
        var path = Path.Combine(_dir, "成绩.xlsx");
        MiniExcelLibs.MiniExcel.SaveAs(path, new[]
        {
            new { 姓名 = "张三", 语文 = 88d },
            new { 姓名 = "李四", 语文 = 76d },
        }, printHeader: true);

        var result = AiDocumentReader.Read(path);

        Assert.Equal(AiDocumentReader.Kind.Spreadsheet, result.Kind);
        Assert.Contains("张三", result.Text, StringComparison.Ordinal);
        Assert.Contains("李四", result.Text, StringComparison.Ordinal);
        AssertNoBinary(result.Text);
    }

    [Fact]
    public void Docx_is_extracted_without_embedded_binary()
    {
        var path = Path.Combine(_dir, "试卷.docx");

        // 手工构造一个 docx：文字节点 + 一段"内嵌图片"的假二进制（模拟真实 docx 里的 PNG 数据块）
        var fakeBinary = Convert.ToBase64String(Enumerable.Range(0, 300).Select(i => (byte)(i % 256)).ToArray());

        var documentXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:body>" +
            "<w:p><w:r><w:t>一、选择题（每题 3 分）</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>1. 下列关于细胞的说法正确的是</w:t></w:r></w:p>" +
            $"<w:p><w:r><w:t>{fakeBinary}</w:t></w:r></w:p>" +
            "</w:body></w:document>";

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(documentXml);
        }

        var result = AiDocumentReader.Read(path);

        Assert.Equal(AiDocumentReader.Kind.Document, result.Kind);
        Assert.True(result.Ok);
        Assert.Contains("一、选择题", result.Text, StringComparison.Ordinal);
        Assert.Contains("下列关于细胞", result.Text, StringComparison.Ordinal);
        // 关键：内嵌的那段 base64 可能混进来，但绝不能出现控制字符
        AssertNoBinary(result.Text);
    }

    [Fact]
    public void Image_is_reported_not_dumped_as_bytes()
    {
        var path = Path.Combine(_dir, "截图.png");
        var bytes = new byte[2048];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        var result = AiDocumentReader.Read(path);

        Assert.Equal(AiDocumentReader.Kind.Image, result.Kind);
        Assert.False(result.Ok);
        Assert.Contains("图片", result.Text, StringComparison.Ordinal);
        AssertNoBinary(result.Text);
    }

    [Fact]
    public void Unknown_binary_is_refused_cleanly()
    {
        var path = Path.Combine(_dir, "程序.bin");
        var bytes = new byte[4096];
        new Random(11).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        var result = AiDocumentReader.Read(path);

        Assert.Equal(AiDocumentReader.Kind.Binary, result.Kind);
        Assert.False(result.Ok);
        Assert.Contains("二进制", result.Text, StringComparison.Ordinal);
        AssertNoBinary(result.Text);
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        var result = AiDocumentReader.Read(Path.Combine(_dir, "不存在.txt"));

        Assert.Equal(AiDocumentReader.Kind.Missing, result.Kind);
        Assert.Contains("不存在", result.Text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // 忽略
        }
    }
}
