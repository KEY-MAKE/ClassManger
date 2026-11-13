using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 成绩单筛选检测的契约。
/// <para>
/// 真实场景：老师给的是「全校成绩表 + Excel 筛选出本班」。
/// 被筛掉的行在文件里只是 <c>hidden="1"</c>，数据仍在 ——
/// 不处理的话导入的是全校几百人，点名完全没法用。
/// </para>
/// </summary>
public class XlsxFilterTests : IDisposable
{
    private readonly string _dir;

    public XlsxFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cm_xlsx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>造一个最小合法 xlsx，可指定哪些数据行隐藏。</summary>
    private string MakeXlsx(string fileName, IReadOnlyList<(string Name, double Score)> students, ISet<int> hiddenIndexes)
    {
        var path = Path.Combine(_dir, fileName);

        var strings = new List<string> { "姓名", "成绩" };
        strings.AddRange(students.Select(s => s.Name));

        var sst = new StringBuilder();
        sst.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sst.Append($"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">");
        foreach (var t in strings)
        {
            sst.Append($"<si><t>{t}</t></si>");
        }
        sst.Append("</sst>");

        var rows = new StringBuilder();
        rows.Append("<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>");

        for (var i = 0; i < students.Count; i++)
        {
            var r = i + 2;
            var hidden = hiddenIndexes.Contains(i) ? " hidden=\"1\"" : string.Empty;
            rows.Append($"<row r=\"{r}\"{hidden}><c r=\"A{r}\" t=\"s\"><v>{2 + i}</v></c><c r=\"B{r}\"><v>{students[i].Score}</v></c></row>");
        }

        var sheet = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                    + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + $"<sheetData>{rows}</sheetData></worksheet>";

        var contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
            + "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>"
            + "</Types>";

        var rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
            + "</Relationships>";

        var workbook = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
            + "<sheets><sheet name=\"成绩\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

        var workbookRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>"
            + "</Relationships>";

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, "[Content_Types].xml", contentTypes);
            Write(zip, "_rels/.rels", rels);
            Write(zip, "xl/workbook.xml", workbook);
            Write(zip, "xl/_rels/workbook.xml.rels", workbookRels);
            Write(zip, "xl/sharedStrings.xml", sst.ToString());
            Write(zip, "xl/worksheets/sheet1.xml", sheet);
        }

        return path;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static List<(string Name, double Score)> TenStudents() => new()
    {
        ("张三", 95), ("李四", 88), ("王五", 76), ("赵六", 91), ("钱七", 83),
        ("孙八", 72), ("周九", 68), ("吴十", 94), ("郑一", 81), ("王二", 77),
    };

    /// <summary>能检出被隐藏的行数和位置。</summary>
    [Fact]
    public void Hidden_rows_are_detected()
    {
        var hidden = new HashSet<int> { 0, 1, 2, 4, 5, 7, 8 };
        var path = MakeXlsx("filtered.xlsx", TenStudents(), hidden);

        var layout = XlsxRowFilter.Analyze(path);

        Assert.NotNull(layout);
        Assert.Equal(1, layout!.HeaderRow);
        Assert.Equal(10, layout.DataRows.Count);
        Assert.Equal(7, layout.HiddenCount);

        // 可见的正好是 index 3 / 6 / 9 → 行号 5 / 8 / 11
        var visible = layout.DataRows.Where(r => !r.Hidden).Select(r => r.RowNumber).ToList();
        Assert.Equal(new[] { 5, 8, 11 }, visible);
    }

    /// <summary>
    /// 核心：导入时只拿到可见的 3 个人，隐藏的 7 个不进名单。
    /// </summary>
    [Fact]
    public void Import_skips_hidden_rows()
    {
        var hidden = new HashSet<int> { 0, 1, 2, 4, 5, 7, 8 };
        var path = MakeXlsx("filtered.xlsx", TenStudents(), hidden);

        var service = new ImportService();
        var result = service.Import(path, generateDemoScoresWhenMissing: true);

        Assert.Equal(3, result.Students.Count);
        Assert.Equal(7, result.SkippedHiddenRows);

        // 导入后会按成绩降序排：赵六 91 / 王二 77 / 周九 68
        var names = result.Students.Select(s => s.Name).ToList();
        Assert.Equal(new[] { "赵六", "王二", "周九" }, names);
        Assert.Null(result.FilterNotice);
    }

    /// <summary>没有筛选的普通表 → 全部导入，不误跳。</summary>
    [Fact]
    public void Unfiltered_sheet_imports_everyone()
    {
        var path = MakeXlsx("all.xlsx", TenStudents(), new HashSet<int>());

        var service = new ImportService();
        var result = service.Import(path, generateDemoScoresWhenMissing: true);

        Assert.Equal(10, result.Students.Count);
        Assert.Equal(0, result.SkippedHiddenRows);
    }

    /// <summary>缺 hidden 属性的行都算可见（老版本 Excel 可能不写这个属性）。</summary>
    [Fact]
    public void Rows_without_hidden_attribute_are_visible()
    {
        var path = MakeXlsx("plain.xlsx", TenStudents().Take(3).ToList(), new HashSet<int>());

        var layout = XlsxRowFilter.Analyze(path);

        Assert.NotNull(layout);
        Assert.Equal(3, layout!.DataRows.Count);
        Assert.Equal(0, layout.HiddenCount);
    }

    /// <summary>非 xlsx / 不存在的文件 → 返回 null，不抛异常。</summary>
    [Fact]
    public void Non_xlsx_returns_null()
    {
        var csv = Path.Combine(_dir, "a.csv");
        File.WriteAllText(csv, "姓名,成绩\n张三,90");

        Assert.Null(XlsxRowFilter.Analyze(csv));
        Assert.Null(XlsxRowFilter.Analyze(Path.Combine(_dir, "nope.xlsx")));
        Assert.Null(XlsxRowFilter.Analyze(string.Empty));
    }

    /// <summary>只有表头没有数据 → 返回 null（谈不上筛选）。</summary>
    [Fact]
    public void Header_only_sheet_returns_null()
    {
        var path = MakeXlsx("empty.xlsx", new List<(string, double)>(), new HashSet<int>());

        Assert.Null(XlsxRowFilter.Analyze(path));
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
