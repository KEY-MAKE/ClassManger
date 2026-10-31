using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>点名引擎接口。</summary>
public interface IRollCallService
{
    /// <summary>当前名单。</summary>
    IReadOnlyList<Student> Students { get; }

    /// <summary>点名历史（会话内）。</summary>
    IReadOnlyList<CallRecord> History { get; }

    /// <summary>统计状态。</summary>
    RollCallState State { get; }

    /// <summary>点名模式。</summary>
    RollCallMode Mode { get; set; }

    /// <summary>是否所有人都已被点到。</summary>
    bool IsAllCalled { get; }

    /// <summary>载入名单并清空记录。</summary>
    void LoadRoster(IReadOnlyList<Student> students);

    /// <summary>清空记录（保留名单）。</summary>
    void Reset();

    /// <summary>执行一次点名；只在「只点未到」且全部点完时返回 null。</summary>
    RollCallResult? Roll();

    /// <summary>顺序模式下一位学生的姓名（供测试与预览）。</summary>
    string NextSequentialName();
}
