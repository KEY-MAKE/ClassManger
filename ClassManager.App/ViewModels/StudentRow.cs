using ClassManager.App.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassManager.App.ViewModels;

/// <summary>点名名单表格行。</summary>
public sealed partial class StudentRow : ObservableObject
{
    public StudentRow(int index, string name, double score)
    {
        Index = index;
        Name = name;
        Score = score;
    }

    /// <summary>序号（1 基）。</summary>
    public int Index { get; }

    /// <summary>姓名。</summary>
    public string Name { get; }

    /// <summary>成绩。</summary>
    public double Score { get; }

    /// <summary>成绩显示文本。</summary>
    public string ScoreText => Score.ToString("0.###");

    [ObservableProperty]
    private bool _isCalled;

    /// <summary>状态显示文本。</summary>
    public string StatusText => IsCalled ? "✅ 已点到" : "⏳ 未点到";

    partial void OnIsCalledChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}
