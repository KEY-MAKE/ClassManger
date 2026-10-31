using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>成绩单导入服务。</summary>
public interface IImportService
{
    /// <summary>支持的文件扩展名（小写，含点）。</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>是否为旧版二进制 .xls（不支持，需给出明确提示）。</summary>
    bool IsLegacyXls(string filePath);

    /// <summary>从文件导入成绩单。</summary>
    ImportResult Import(string filePath, bool generateDemoScoresWhenMissing);

    /// <summary>从已解析的行集合导入（纯逻辑，供测试）。</summary>
    ImportResult ImportFromRows(
        IEnumerable<IDictionary<string, object?>> rows,
        string className,
        bool generateDemoScoresWhenMissing);
}
