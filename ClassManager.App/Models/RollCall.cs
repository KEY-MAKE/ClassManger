namespace ClassManager.App.Models;

/// <summary>点名模式。</summary>
public enum RollCallMode
{
    /// <summary>随机点名（允许重复点到）。</summary>
    Random,

    /// <summary>顺序点名（按名单依次点，点完从头开始）。</summary>
    Sequential,

    /// <summary>只点未到（仅从未点到的学生中抽取）。</summary>
    UncalledOnly,
}

/// <summary>一次点名的历史记录。</summary>
public sealed record CallRecord(string StudentName, DateTime Timestamp, RollCallMode Mode);

/// <summary>点名统计状态。</summary>
public sealed record RollCallState(int Total, int Called, int Remaining)
{
    /// <summary>完成度百分比（保留 1 位小数）。</summary>
    public double CompletionPercent => Total <= 0 ? 0d : Math.Round(Called * 100d / Total, 1);
}

/// <summary>点名结果。</summary>
public sealed record RollCallResult(Student Student, DateTime Timestamp, RollCallMode Mode, bool AlreadyCalled);
