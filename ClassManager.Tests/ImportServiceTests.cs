using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>成绩单导入服务单元测试。</summary>
public class ImportServiceTests
{
    private static List<IDictionary<string, object?>> Rows(params (string Name, object? Score)[] data)
    {
        var rows = new List<IDictionary<string, object?>>();
        foreach (var (name, score) in data)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["姓名"] = name,
                ["成绩"] = score,
            });
        }

        return rows;
    }

    [Fact]
    public void ImportFromRows_RecognizesStandardColumns()
    {
        var service = new ImportService();

        var result = service.ImportFromRows(Rows(("张三", 88), ("李四", 95)), "高二3班", true);

        Assert.Equal("姓名", result.NameColumn);
        Assert.Equal("成绩", result.ScoreColumn);
        Assert.False(result.UsedFallbackNameColumn);
        Assert.False(result.GeneratedDemoScores);
        Assert.Equal(2, result.Students.Count);
        Assert.Equal("高二3班", result.ClassName);
    }

    [Fact]
    public void ImportFromRows_SortsDescendingByScore()
    {
        var service = new ImportService();

        var result = service.ImportFromRows(Rows(("张三", 72), ("李四", 95), ("王五", 88)), "班", false);

        Assert.Equal(new[] { "李四", "王五", "张三" }, result.Students.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void ImportFromRows_MissingScoreColumn_GeneratesDemoScores()
    {
        var service = new ImportService();
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["姓名"] = "张三" },
            new Dictionary<string, object?> { ["姓名"] = "李四" },
        };

        var result = service.ImportFromRows(rows, "班", true);

        Assert.True(result.GeneratedDemoScores);
        Assert.All(result.Students, s => Assert.InRange(s.Score, 60, 100));
    }

    [Fact]
    public void ImportFromRows_MissingScoreColumn_NoDemo_FillsZero()
    {
        var service = new ImportService();
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["姓名"] = "张三" },
        };

        var result = service.ImportFromRows(rows, "班", false);

        Assert.False(result.GeneratedDemoScores);
        Assert.Equal(0d, result.Students[0].Score);
    }

    [Fact]
    public void ImportFromRows_NonStandardNameColumn_FallsBackToFirstColumn()
    {
        var service = new ImportService();
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["学生"] = "张三", ["成绩"] = 80 },
        };

        var result = service.ImportFromRows(rows, "班", false);

        Assert.True(result.UsedFallbackNameColumn);
        Assert.Equal("学生", result.NameColumn);
        Assert.NotNull(result.NameColumnNotice);
        Assert.Contains("第一列", result.NameColumnNotice!);
    }

    [Fact]
    public void ImportFromRows_CleanseBlankNames()
    {
        var service = new ImportService();
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["姓名"] = "  ", ["成绩"] = 10 },
            new Dictionary<string, object?> { ["姓名"] = "张三", ["成绩"] = 80 },
            new Dictionary<string, object?> { ["姓名"] = null, ["成绩"] = 50 },
        };

        var result = service.ImportFromRows(rows, "班", false);

        Assert.Single(result.Students);
        Assert.Equal("张三", result.Students[0].Name);
    }

    [Fact]
    public void ImportFromRows_EmptyRows_Throws()
    {
        var service = new ImportService();

        Assert.Throws<ImportException>(() =>
            service.ImportFromRows(new List<IDictionary<string, object?>>(), "班", true));
    }

    [Fact]
    public void IsLegacyXls_DetectsDotXls()
    {
        var service = new ImportService();

        Assert.True(service.IsLegacyXls("C:/data/old.xls"));
        Assert.True(service.IsLegacyXls("C:/data/OLD.XLS"));
        Assert.False(service.IsLegacyXls("C:/data/new.xlsx"));
        Assert.False(service.IsLegacyXls("C:/data/new.csv"));
    }

    [Fact]
    public void Import_LegacyXls_ThrowsWithClearMessage()
    {
        var service = new ImportService();

        var ex = Assert.Throws<ImportException>(() => service.Import("C:/data/old.xls", true));
        Assert.Contains(".xls", ex.Message);
    }

    [Fact]
    public void SupportedExtensions_ContainsCsvAndXlsx()
    {
        var service = new ImportService();

        Assert.Contains(".csv", service.SupportedExtensions);
        Assert.Contains(".xlsx", service.SupportedExtensions);
        Assert.DoesNotContain(".xls", service.SupportedExtensions);
    }
}
