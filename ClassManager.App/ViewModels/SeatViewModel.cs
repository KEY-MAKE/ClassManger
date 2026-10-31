using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ClassManager.App.Models;
using ClassManager.App.Services;
using ClassManager.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClassManager.App.ViewModels;

/// <summary>
/// 座位页 ViewModel：布局 / 三种算法 / 学生按钮 / 选中态 / 手动放置 / 输出入口。
/// </summary>
public sealed partial class SeatViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly ISeatingService _seating;
    private readonly IExportService _export;
    private readonly ISettingsService _settings;

    /// <summary>学生按钮集合。</summary>
    public ObservableCollection<StudentChip> StudentChips { get; } = new();

    [ObservableProperty]
    private int _rows;

    [ObservableProperty]
    private int _cols;

    [ObservableProperty]
    private string? _selectedStudentName;

    [ObservableProperty]
    private int _layoutVersion;

    [ObservableProperty]
    private bool _hasSeats;

    /// <summary>画布是否显示分组表头与排标签（镜像 SeatLayout.ShowHeaders）。</summary>
    [ObservableProperty]
    private bool _showHeaders;

    public SeatViewModel(IShell shell)
    {
        _shell = shell;
        _seating = AppServices.Seating;
        _export = AppServices.Export;
        _settings = AppServices.Settings;

        _rows = _seating.Layout.Rows;
        _cols = _seating.Layout.Cols;
        _showHeaders = _seating.Layout.ShowHeaders;

        AppServices.RosterImported += OnRosterImported;
        LoadFromService();
    }

    partial void OnShowHeadersChanged(bool value)
    {
        _seating.Layout.ShowHeaders = value;
        LayoutVersion++;
    }

    /// <summary>是否有名单数据。</summary>
    public bool HasData => StudentChips.Count > 0;

    private void OnRosterImported(object? sender, ImportResult result)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            SelectedStudentName = null;
            LoadFromService();
            LayoutVersion++;
        });
    }

    private void LoadFromService()
    {
        StudentChips.Clear();
        foreach (var student in _seating.Students)
        {
            StudentChips.Add(new StudentChip(student.Name, student.Score));
        }

        Rows = _seating.Layout.Rows;
        Cols = _seating.Layout.Cols;
        ShowHeaders = _seating.Layout.ShowHeaders;
        RefreshChipStates();
        OnPropertyChanged(nameof(HasData));
    }

    [RelayCommand]
    private Task Import() => _shell.ImportRosterAsync();

    [RelayCommand]
    private void UpdateLayout()
    {
        if (!HasData)
        {
            _shell.ShowToast("提示", "请先导入班级成绩单", ControlAppearance.Caution);
            return;
        }

        try
        {
            _seating.UpdateLayout(Rows, Cols);
            AfterLayoutChanged($"座位布局已更新: {_seating.Layout.Rows}行×{_seating.Layout.Cols}列");
        }
        catch (ArgumentOutOfRangeException)
        {
            _shell.ShowToast("错误", "行数必须在1-20之间，列数必须在1-20之间", ControlAppearance.Danger);
        }
    }

    /// <summary>打开「间距设置」窗口（逐道行/列间隙 + 分组阈值）。</summary>
    [RelayCommand]
    private void GapSettings()
    {
        var window = new GapSettingsWindow(_seating.Layout)
        {
            Owner = Application.Current?.MainWindow,
        };

        if (window.ShowDialog() == true)
        {
            AfterLayoutChanged("间距设置已更新");
        }
    }

    [RelayCommand]
    private void Snake() => Arrange(SeatAlgorithm.Snake, "已按成绩蛇形排列");

    [RelayCommand]
    private void Grouped() => Arrange(SeatAlgorithm.Grouped, "已按成绩分组排列");

    [RelayCommand]
    private void Random() => Arrange(SeatAlgorithm.Random, "已随机排列");

    private void Arrange(SeatAlgorithm algorithm, string message)
    {
        if (!HasData)
        {
            _shell.ShowToast("提示", "请先导入班级成绩单", ControlAppearance.Caution);
            return;
        }

        _seating.ArrangeAuto(algorithm);
        AfterLayoutChanged($"{message} {_seating.Layout.OccupiedSeats} 个座位");
        _shell.ShowToast("排座完成", message, ControlAppearance.Success);
    }

    /// <summary>
    /// 导入座位表：只接受本程序导出的 xlsx，并按文件里记录的版式参数
    /// （行列数、逐道行/列间距、表头开关）完整还原排版。
    /// </summary>
    [RelayCommand]
    private void ImportSeatSheet()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入座位表（仅支持本程序导出的 xlsx）",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
        };

        ApplyExportDir(dialog);

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!SeatSheetImporter.TryImport(dialog.FileName, _seating.Layout, out var message))
        {
            _shell.ShowToast("导入失败", message, ControlAppearance.Danger);
            return;
        }

        // 把还原后的版式同步到界面上的控件
        Rows = _seating.Layout.Rows;
        Cols = _seating.Layout.Cols;
        ShowHeaders = _seating.Layout.ShowHeaders;

        RememberExportDir(dialog.FileName);
        AfterLayoutChanged(message);
        _shell.ShowToast("导入成功", message, ControlAppearance.Success);
    }

    [RelayCommand]
    private void SaveXlsx()
    {
        if (_seating.Layout.OccupiedSeats == 0)
        {
            _shell.ShowToast("提示", "没有座位安排可保存", ControlAppearance.Caution);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存座位表",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            FileName = $"seat_{DateTime.Now:yyyyMMdd}.xlsx",
        };

        ApplyExportDir(dialog);

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _export.ExportSeatXlsx(
                dialog.FileName,
                _seating.Layout,
                AppServices.CurrentClassName,
                DateTime.Now);

            RememberExportDir(dialog.FileName);
            _shell.SetStatus($"座位表已保存: {Path.GetFileName(dialog.FileName)}");
            _shell.ShowToast("保存成功", $"座位表已保存: {Path.GetFileName(dialog.FileName)}", ControlAppearance.Success);
        }
        catch (Exception ex)
        {
            ShowError($"保存失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Print()
    {
        if (_seating.Layout.OccupiedSeats == 0)
        {
            _shell.ShowToast("提示", "没有座位安排可打印", ControlAppearance.Caution);
            return;
        }

        var options = AskExportOptions();
        if (options is null)
        {
            return;
        }

        var window = new SeatPrintWindow(
            _export,
            _seating.Layout,
            AppServices.CurrentClassName,
            options.Value.Headers,
            options.Value.ClassName)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private void ExportImage()
    {
        if (_seating.Layout.OccupiedSeats == 0)
        {
            _shell.ShowToast("提示", "没有座位安排可导出", ControlAppearance.Caution);
            return;
        }

        var options = AskExportOptions();
        if (options is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出座位表为图片",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
            DefaultExt = ".png",
            FileName = $"seat_{DateTime.Now:yyyyMMdd}.png",
        };

        ApplyExportDir(dialog);

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var jpeg = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                   || dialog.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        try
        {
            _export.ExportSeatImage(
                dialog.FileName,
                _seating.Layout,
                AppServices.CurrentClassName,
                options.Value.Headers,
                options.Value.ClassName,
                jpeg);

            RememberExportDir(dialog.FileName);
            _shell.SetStatus($"座位表已导出为图片: {Path.GetFileName(dialog.FileName)}");
            _shell.ShowToast("导出成功", $"座位表已导出为图片: {Path.GetFileName(dialog.FileName)}", ControlAppearance.Success);
        }
        catch (Exception ex)
        {
            ShowError($"导出失败: {ex.Message}");
        }
    }

    /// <summary>导出 / 打印选项的两个勾选结果。</summary>
    private readonly record struct ExportOptions(bool Headers, bool ClassName);

    /// <summary>弹出导出/打印选项；返回 null 表示用户取消。</summary>
    private ExportOptions? AskExportOptions()
    {
        var window = new SeatExportOptionsWindow(_seating.Layout.ShowHeaders, _seating.Layout.ShowHeaders)
        {
            Owner = Application.Current?.MainWindow,
        };

        return window.ShowDialog() == true
            ? new ExportOptions(window.IncludeHeaders, window.IncludeClassName)
            : null;
    }

    [RelayCommand]
    private void SelectStudent(string? name)
    {
        SelectedStudentName = name;

        foreach (var chip in StudentChips)
        {
            chip.IsSelected = string.Equals(chip.Name, name, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(name))
        {
            var score = _seating.Students.FirstOrDefault(s => s.Name == name)?.Score ?? 0d;
            _shell.SetStatus($"已选择学生: {name} ({score:0.###}分)，请点击座位放置");
        }
    }

    /// <summary>
    /// 把学生拖到座位上（拖放路径：不需要先点选学生）。
    /// 目标座位空着就放入；该学生已在别处则移动过去。
    /// </summary>
    public void AssignStudentToSeat(string studentName, int row, int col)
    {
        if (string.IsNullOrWhiteSpace(studentName))
        {
            return;
        }

        try
        {
            var student = _seating.Students.FirstOrDefault(s => s.Name == studentName);
            if (student is null)
            {
                return;
            }

            if (_seating.TryPlace(student, row, col, out var move))
            {
                AfterLayoutChanged($"{student.Name} 已拖到座位{DescribeSeat(row, col)}");
                return;
            }

            if (move?.From is { } from)
            {
                _seating.MoveTo(student, row, col);
                AfterLayoutChanged($"{student.Name} 已从座位{DescribeSeat(from.Row, from.Col)} 移到{DescribeSeat(row, col)}");
                return;
            }

            _shell.SetStatus($"座位{DescribeSeat(row, col)}已被占用，请先清空该座位");
        }
        catch (Exception ex)
        {
            ShowError($"拖放失败: {ex.Message}");
        }
    }

    /// <summary>清空当前选中（Esc）。</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        SelectedStudentName = null;
        foreach (var chip in StudentChips)
        {
            chip.IsSelected = false;
        }
    }

    /// <summary>座位被点击（由 SeatMapControl 桥接）。</summary>
    [RelayCommand]
    private void SeatClicked(SeatPosition? position)
    {
        if (position is { } pos)
        {
            OnSeatClicked(pos.Row, pos.Col);
        }
    }

    /// <summary>处理座位点击：占用→确认清空；选中学生→放置 / 确认移动；空点→提示先选学生。</summary>
    public async void OnSeatClicked(int row, int col)
    {
        try
        {
            if (!HasData)
            {
                return;
            }

            var existing = _seating.GetStudentAt(row, col);
            if (existing is not null)
            {
                var clear = await ConfirmAsync("清空座位", $"座位{DescribeSeat(row, col)}已有学生: {existing}\n是否清空此座位？");
                if (clear)
                {
                    _seating.ClearSeat(row, col);
                    AfterLayoutChanged($"已清空座位{DescribeSeat(row, col)}");
                }

                return;
            }

            if (string.IsNullOrEmpty(SelectedStudentName))
            {
                await ShowMessageAsync("提示", "请先点击学生列表选择学生");
                return;
            }

            var student = _seating.Students.FirstOrDefault(s => s.Name == SelectedStudentName);
            if (student is null)
            {
                return;
            }

            if (_seating.TryPlace(student, row, col, out var move))
            {
                AfterLayoutChanged($"{student.Name} 已安排到座位{DescribeSeat(row, col)}");
                return;
            }

            if (move?.From is { } from)
            {
                var doMove = await ConfirmAsync(
                    "移动学生",
                    $"{student.Name}已在座位{DescribeSeat(from.Row, from.Col)}\n是否移动到新座位{DescribeSeat(row, col)}？");

                if (doMove)
                {
                    _seating.MoveTo(student, row, col);
                    AfterLayoutChanged($"{student.Name} 已移动到座位{DescribeSeat(row, col)}");
                }
            }
        }
        catch (Exception ex)
        {
            // async void 事件处理器：异常无法向上传播，若不在此兜底会经
            // DispatcherUnhandledException 直接崩溃进程。这里给出可读错误提示（不静默吞掉）。
            ShowError($"座位操作失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 座位可读标签：讲台侧位显示「讲台左」/「讲台右」，网格座位显示 (行, 列)。
    /// </summary>
    public static string DescribeSeat(int row, int col)
        => SeatLayout.IsPlatformSeat(row)
            ? (col == SeatLayout.PlatformSeatLeftCol ? "讲台左" : "讲台右")
            : $"({row + 1}, {col + 1})";

    private void AfterLayoutChanged(string status)
    {
        LayoutVersion++;
        HasSeats = _seating.Layout.OccupiedSeats > 0;
        RefreshChipStates();
        _shell.SetStatus(status);
    }

    private void RefreshChipStates()
    {
        foreach (var chip in StudentChips)
        {
            chip.IsSeated = _seating.IsSeated(chip.Name);
        }
    }

    private void ApplyExportDir(FileDialog dialog)
    {
        var lastDir = _settings.Current.LastExportDirectory;
        if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
        {
            dialog.InitialDirectory = lastDir;
        }
    }

    private void RememberExportDir(string filePath)
    {
        _settings.Current.LastExportDirectory = Path.GetDirectoryName(filePath);
        _settings.Save();
    }

    private void ShowError(string message)
    {
        _shell.SetStatus(message);
        _shell.ShowToast("错误", message, ControlAppearance.Danger);
    }

    private static async Task<bool> ConfirmAsync(string title, string content)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "是",
            CloseButtonText = "否",
        };

        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    private static async Task ShowMessageAsync(string title, string content)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "确定",
            CloseButtonText = string.Empty,
        };

        await box.ShowDialogAsync();
    }
}
