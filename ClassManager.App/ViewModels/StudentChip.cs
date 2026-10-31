using ClassManager.App.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassManager.App.ViewModels;

/// <summary>座位页学生按钮条目。</summary>
public sealed partial class StudentChip : ObservableObject
{
    public StudentChip(string name, double score)
    {
        Name = name;
        Score = score;
    }

    /// <summary>姓名。</summary>
    public string Name { get; }

    /// <summary>成绩。</summary>
    public double Score { get; }

    /// <summary>显示文本「姓名 (成绩分)」。</summary>
    public string DisplayText => $"{Name} ({Score:0.###}分)";

    [ObservableProperty]
    private bool _isSeated;

    [ObservableProperty]
    private bool _isSelected;
}
