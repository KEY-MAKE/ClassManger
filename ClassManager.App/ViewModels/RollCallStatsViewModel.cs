using ClassManager.App.Models;
using ClassManager.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassManager.App.ViewModels;

/// <summary>点名统计弹窗 ViewModel：总/已/未/完成度 + 最近 5 条（倒序）。</summary>
public sealed partial class RollCallStatsViewModel : ObservableObject
{
    public RollCallStatsViewModel(IRollCallService rollCall)
    {
        Total = rollCall.State.Total;
        Called = rollCall.State.Called;
        Remaining = rollCall.State.Remaining;
        CompletionPercent = rollCall.State.CompletionPercent;

        RecentRecords = rollCall.History
            .Reverse()
            .Take(5)
            .Select((record, index) => new RollCallHistoryItem(
                index + 1,
                record.StudentName,
                record.Timestamp.ToString("HH:mm:ss"),
                record.Mode))
            .ToList();
    }

    public int Total { get; }

    public int Called { get; }

    public int Remaining { get; }

    public double CompletionPercent { get; }

    /// <summary>最近 5 条记录（倒序）。</summary>
    public IReadOnlyList<RollCallHistoryItem> RecentRecords { get; }

    /// <summary>是否无历史记录。</summary>
    public bool HasRecords => RecentRecords.Count > 0;

    /// <summary>完成度文本（保留 1 位小数）。</summary>
    public string CompletionText => $"{CompletionPercent:0.0}%";
}

/// <summary>统计弹窗中的一条历史记录。</summary>
public sealed record RollCallHistoryItem(int Index, string StudentName, string Time, RollCallMode Mode)
{
    /// <summary>记录显示文本。</summary>
    public string DisplayText => $"{Index}. {StudentName} ({Time})";
}
