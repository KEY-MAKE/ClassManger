namespace ClassManager.App.Models;

/// <summary>成绩单导入结果。</summary>
public sealed record ImportResult(
    IReadOnlyList<Student> Students,
    string FilePath,
    string ClassName,
    string? NameColumn,
    string? ScoreColumn,
    bool UsedFallbackNameColumn,
    bool GeneratedDemoScores,
    string? NameColumnNotice);

/// <summary>导入过程中的可读异常（UI 直接展示 Message）。</summary>
public sealed class ImportException : Exception
{
    public ImportException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
