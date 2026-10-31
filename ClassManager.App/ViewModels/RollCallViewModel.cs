using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using ClassManager.App.Models;
using ClassManager.App.Services;
using ClassManager.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace ClassManager.App.ViewModels;

/// <summary>
/// 点名页 ViewModel：模式 / 统计 / 名单行 / 搜索 / 结果 / 重置 / 统计弹窗。
/// </summary>
public sealed partial class RollCallViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly IRollCallService _roll;

    /// <summary>名单行集合。</summary>
    public ObservableCollection<StudentRow> Students { get; } = new();

    /// <summary>过滤后的视图（按 SearchText 模糊匹配，忽略大小写）。</summary>
    public ICollectionView FilteredStudents { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private RollCallMode _mode = RollCallMode.Random;

    [ObservableProperty]
    private string _resultText = "等待点名...";

    /// <summary>是否已有一次点名结果（用于结果区「大屏」样式与空态切换）。</summary>
    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private int _called;

    [ObservableProperty]
    private int _remaining;

    [ObservableProperty]
    private double _completionPercent;

    public RollCallViewModel(IShell shell)
    {
        _shell = shell;
        _roll = AppServices.RollCall;

        FilteredStudents = CollectionViewSource.GetDefaultView(Students);
        FilteredStudents.Filter = FilterPredicate;

        AppServices.RosterImported += OnRosterImported;
        LoadFromService();
    }

    /// <summary>是否有名单数据。</summary>
    public bool HasData => Total > 0;

    /// <summary>模式文案（供界面辅助说明）。</summary>
    public string ModeHint => Mode switch
    {
        RollCallMode.Random => "随机抽取，允许重复点到",
        RollCallMode.Sequential => "按名单顺序依次点，点完从头开始",
        _ => "只从未点到的学生中抽取",
    };

    partial void OnSearchTextChanged(string value) => FilteredStudents.Refresh();

    partial void OnModeChanged(RollCallMode value)
    {
        _roll.Mode = value;
        OnPropertyChanged(nameof(ModeHint));
    }

    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(HasData));

    [RelayCommand]
    private Task Import() => _shell.ImportRosterAsync();

    [RelayCommand]
    private void Roll()
    {
        if (!HasData)
        {
            _shell.ShowToast("提示", "请先导入班级成绩单", ControlAppearance.Caution);
            return;
        }

        _roll.Mode = Mode;
        var result = _roll.Roll();
        if (result is null)
        {
            _shell.ShowToast("点名完成", "所有学生都已点到！", ControlAppearance.Info);
            return;
        }

        ResultText = result.Student.Name;
        HasResult = true;
        RefreshCalledStates();
        UpdateStats();
        _shell.SetStatus($"点到: {result.Student.Name} - {result.Timestamp:HH:mm:ss}");
    }

    [RelayCommand]
    private async Task ResetRoll()
    {
        if (!HasData)
        {
            return;
        }

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "确认重置",
            Content = "确定要重置所有点名记录吗？",
            PrimaryButtonText = "是",
            CloseButtonText = "否",
        };

        var result = await confirm.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        _roll.Reset();
        ResultText = "等待点名...";
        HasResult = false;
        RefreshCalledStates();
        UpdateStats();
        _shell.SetStatus("点名记录已重置");
        _shell.ShowToast("已重置", "点名记录已清空", ControlAppearance.Success);
    }

    [RelayCommand]
    private void ShowStats()
    {
        if (!HasData)
        {
            _shell.ShowToast("提示", "没有学生数据", ControlAppearance.Caution);
            return;
        }

        var window = new RollCallStatsWindow(_roll)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private void ClearResult()
    {
        ResultText = "等待点名...";
        HasResult = false;
    }

    private void OnRosterImported(object? sender, ImportResult result)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ResultText = "等待点名...";
            HasResult = false;
            LoadFromService();
        });
    }

    private void LoadFromService()
    {
        Students.Clear();
        var index = 1;
        foreach (var student in _roll.Students)
        {
            Students.Add(new StudentRow(index++, student.Name, student.Score));
        }

        UpdateStats();
    }

    private void RefreshCalledStates()
    {
        var calledNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _roll.History)
        {
            calledNames.Add(record.StudentName);
        }

        foreach (var row in Students)
        {
            row.IsCalled = calledNames.Contains(row.Name);
        }
    }

    private void UpdateStats()
    {
        var state = _roll.State;
        Total = state.Total;
        Called = state.Called;
        Remaining = state.Remaining;
        CompletionPercent = state.CompletionPercent;
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return obj is StudentRow row
            && row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
}
