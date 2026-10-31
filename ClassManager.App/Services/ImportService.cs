using System.IO;
using System.Text;
using ClassManager.App.Models;
using MiniExcelLibs;

namespace ClassManager.App.Services;

/// <summary>
/// 成绩单导入实现：列自动识别、缺失成绩处理、清洗、降序排序、旧版 .xls 拒绝。
/// </summary>
public sealed class ImportService : IImportService
{
    private static readonly string[] NameCandidates =
    {
        "姓名", "名字", "学生姓名", "学生名字", "name", "Name", "student", "Student",
    };

    private static readonly string[] ScoreCandidates =
    {
        "成绩", "分数", "总分", "score", "Score", "grade", "Grade", "总成绩",
    };

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".csv", ".xlsx" };

    /// <inheritdoc />
    public bool IsLegacyXls(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".xls", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ImportResult Import(string filePath, bool generateDemoScoresWhenMissing)
    {
        if (IsLegacyXls(filePath))
        {
            throw new ImportException("暂不支持旧版 .xls（二进制）文件，请用 Excel 另存为 .xlsx 或 .csv 后重试。");
        }

        if (!File.Exists(filePath))
        {
            throw new ImportException($"文件不存在: {filePath}");
        }

        var className = Path.GetFileNameWithoutExtension(filePath);

        try
        {
            var rows = ReadRows(filePath);
            var result = ImportFromRows(rows, className, generateDemoScoresWhenMissing);
            return result with { FilePath = filePath };
        }
        catch (ImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ImportException($"读取文件时出错: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public ImportResult ImportFromRows(
        IEnumerable<IDictionary<string, object?>> rows,
        string className,
        bool generateDemoScoresWhenMissing)
    {
        var rowList = rows as IList<IDictionary<string, object?>> ?? rows.ToList();
        if (rowList.Count == 0)
        {
            throw new ImportException("文件中没有可用的数据行。");
        }

        // 汇总所有出现过的列名（保持首次出现顺序）
        var columns = new List<string>();
        foreach (var row in rowList)
        {
            foreach (var key in row.Keys)
            {
                if (!columns.Contains(key, StringComparer.Ordinal))
                {
                    columns.Add(key);
                }
            }
        }

        if (columns.Count == 0)
        {
            throw new ImportException("未找到任何列，请检查文件内容。");
        }

        // 姓名列识别（回退第一列）
        var nameColumn = columns.FirstOrDefault(c => NameCandidates.Contains(c, StringComparer.OrdinalIgnoreCase));
        var usedFallback = nameColumn is null;
        nameColumn ??= columns[0];
        var nameNotice = usedFallback
            ? $"未找到标准姓名列，将使用第一列「{nameColumn}」作为学生姓名。"
            : null;

        // 成绩列识别
        var scoreColumn = columns.FirstOrDefault(c => ScoreCandidates.Contains(c, StringComparer.OrdinalIgnoreCase));
        var generated = false;
        if (scoreColumn is null && generateDemoScoresWhenMissing)
        {
            generated = true;
        }

        var students = new List<Student>();
        foreach (var row in rowList)
        {
            if (!row.TryGetValue(nameColumn, out var rawName))
            {
                continue;
            }

            var name = rawName?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            double score;
            if (generated)
            {
                score = Random.Shared.Next(60, 101);
            }
            else if (scoreColumn is not null && row.TryGetValue(scoreColumn, out var rawScore))
            {
                score = ParseScore(rawScore);
            }
            else
            {
                score = 0d;
            }

            students.Add(new Student(name, score));
        }

        if (students.Count == 0)
        {
            throw new ImportException("未解析到任何学生（姓名列可能为空）。");
        }

        // 按姓名去重（保留首次出现），随后按成绩降序排序
        var unique = new List<Student>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var student in students)
        {
            if (seen.Add(student.Name))
            {
                unique.Add(student);
            }
        }

        var ordered = unique
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new ImportResult(
            ordered,
            string.Empty,
            className,
            nameColumn,
            scoreColumn,
            usedFallback,
            generated,
            nameNotice);
    }

    /// <summary>读取文件为行集合（自动处理 CSV 编码与 xlsx）。</summary>
    private static List<IDictionary<string, object?>> ReadRows(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // 关键：对 Stream 调用 Query 时必须显式指定 excelType。
        // 否则 MiniExcel 无法从流推断格式（MemoryStream 无扩展名/魔数）会抛
        // "The file type could not be inferred automatically, please specify ExcelType manually"。
        var excelType = ext == ".xlsx" ? ExcelType.XLSX : ExcelType.CSV;

        using var stream = PrepareStream(filePath, ext);

        var list = new List<IDictionary<string, object?>>();
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: true, excelType: excelType))
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (row is IDictionary<string, object> source)
            {
                foreach (var kv in source)
                {
                    dict[kv.Key] = kv.Value;
                }
            }

            list.Add(dict);
        }

        return list;
    }

    /// <summary>
    /// 为 xlsx 返回文件流；为 CSV 做 BOM/UTF-8/GBK 编码探测后解码为字符串，
    /// 再以 UTF-8 重新编码写入新的 MemoryStream（避免 MiniExcel 按 UTF-8 硬读导致 GBK 中文表头乱码）。
    /// 调用方 ReadRows 会显式传入 excelType，无需依赖魔数推断。
    /// </summary>
    private static Stream PrepareStream(string filePath, string ext)
    {
        if (ext == ".xlsx")
        {
            return File.OpenRead(filePath);
        }

        var bytes = File.ReadAllBytes(filePath);
        string text;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    text = GetGbkEncoding().GetString(bytes);
                }
                catch
                {
                    text = Encoding.UTF8.GetString(bytes);
                }
            }
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    private static Encoding GetGbkEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GBK");
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static double ParseScore(object? value)
    {
        switch (value)
        {
            case null:
                return 0d;
            case double d:
                return d;
            case float f:
                return f;
            case int i:
                return i;
            case long l:
                return l;
            case decimal m:
                return (double)m;
            default:
                return double.TryParse(value.ToString(), out var parsed) ? parsed : 0d;
        }
    }
}
