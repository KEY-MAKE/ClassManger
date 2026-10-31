using System.IO;
using System.Text;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// CSV 导入整链路集成测试（真实写文件 → ImportService.Import(path)）：
/// UTF-8(带BOM) / UTF-8(无BOM) / GBK(含中文表头) 三种编码均须正确解析。
/// 每个测试实例使用独立的临时目录，并在 Dispose 中彻底清理，避免在 %TEMP% 留下残留。
/// </summary>
public class CsvImportIntegrationTests : IDisposable
{
    private const string CsvContent = "姓名,成绩\n张三,88\n李四,95\n王五,72\n";

    private readonly string _dir;

    public CsvImportIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cm_csv_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>测试结束（无论成功/失败）删除临时目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch
        {
            // 清理失败不影响测试结果；下次运行使用新的随机目录。
        }
    }

    private (string Path, ImportResult Result) ImportWithEncoding(string fileName, Encoding encoding, bool bom)
    {
        var path = Path.Combine(_dir, fileName);

        var bytes = encoding.GetBytes(CsvContent);
        if (bom)
        {
            var preamble = encoding.GetPreamble();
            var combined = new byte[preamble.Length + bytes.Length];
            Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
            Buffer.BlockCopy(bytes, 0, combined, preamble.Length, bytes.Length);
            File.WriteAllBytes(path, combined);
        }
        else
        {
            File.WriteAllBytes(path, bytes);
        }

        var service = new ImportService();
        var result = service.Import(path, true);
        return (path, result);
    }

    [Fact]
    public void Import_Csv_AllThreeEncodings_ParseCorrectly()
    {
        // 确保 GBK 编码可用
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding("GBK");

        var utf8Bom = ImportWithEncoding("utf8_bom.csv", new UTF8Encoding(true), true);
        var utf8NoBom = ImportWithEncoding("utf8_nobom.csv", new UTF8Encoding(false), false);
        var gbkImport = ImportWithEncoding("gbk_chinese.csv", gbk, false);

        foreach (var result in new[] { utf8Bom.Result, utf8NoBom.Result, gbkImport.Result })
        {
            Assert.Equal(3, result.Students.Count);
            Assert.Equal("姓名", result.NameColumn);
            Assert.Equal("成绩", result.ScoreColumn);
            Assert.Equal(new[] { "李四", "张三", "王五" }, result.Students.Select(s => s.Name).ToArray());
            Assert.Equal(new[] { 95d, 88d, 72d }, result.Students.Select(s => s.Score).ToArray());
        }
    }
}
