using System;
using System.Reflection;
using ClassManager.App.ViewModels;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// XAML 里写的 <c>Command="{Binding XxxCommand}"</c> 编译期不校验：命令属性不存在时
/// 只是静默不响应（按钮点了没反应）。这组用例把「页面用到哪些命令」钉死，
/// 一旦某个 <c>[RelayCommand]</c> 被误删就会立刻失败。
/// 新增按钮时请同步在这里登记。
/// </summary>
public class ViewModelCommandContractTests
{
    [Theory]
    // SeatPage.xaml
    [InlineData(typeof(SeatViewModel), "ClearSelectionCommand")]
    [InlineData(typeof(SeatViewModel), "ExportImageCommand")]
    [InlineData(typeof(SeatViewModel), "GapSettingsCommand")]
    [InlineData(typeof(SeatViewModel), "GroupedCommand")]
    [InlineData(typeof(SeatViewModel), "ImportCommand")]
    [InlineData(typeof(SeatViewModel), "ImportSeatSheetCommand")]
    [InlineData(typeof(SeatViewModel), "PrintCommand")]
    [InlineData(typeof(SeatViewModel), "RandomCommand")]
    [InlineData(typeof(SeatViewModel), "SaveXlsxCommand")]
    [InlineData(typeof(SeatViewModel), "SnakeCommand")]
    [InlineData(typeof(SeatViewModel), "UpdateLayoutCommand")]
    // RollCallPage.xaml
    [InlineData(typeof(RollCallViewModel), "ClearResultCommand")]
    [InlineData(typeof(RollCallViewModel), "ImportCommand")]
    [InlineData(typeof(RollCallViewModel), "ResetRollCommand")]
    [InlineData(typeof(RollCallViewModel), "RollCommand")]
    [InlineData(typeof(RollCallViewModel), "ShowStatsCommand")]
    // Exam2PptPage.xaml
    [InlineData(typeof(Exam2PptViewModel), "BrowseAnswerCommand")]
    [InlineData(typeof(Exam2PptViewModel), "ConfigureBackgroundCommand")]
    [InlineData(typeof(Exam2PptViewModel), "BrowseExamCommand")]
    [InlineData(typeof(Exam2PptViewModel), "BrowseOutputCommand")]
    [InlineData(typeof(Exam2PptViewModel), "BrowseTemplateCommand")]
    [InlineData(typeof(Exam2PptViewModel), "CancelCommand")]
    [InlineData(typeof(Exam2PptViewModel), "OpenOutputFolderCommand")]
    [InlineData(typeof(Exam2PptViewModel), "PreviewStructureCommand")]
    [InlineData(typeof(Exam2PptViewModel), "ShowHelpCommand")]
    [InlineData(typeof(Exam2PptViewModel), "StartCommand")]
    // AboutWindow.xaml
    [InlineData(typeof(AboutViewModel), "CheckUpdateCommand")]
    [InlineData(typeof(AboutViewModel), "ConfigureUpdateSourceCommand")]
    [InlineData(typeof(AboutViewModel), "DownloadUpdateCommand")]
    [InlineData(typeof(AboutViewModel), "IgnoreUpdateCommand")]
    [InlineData(typeof(AboutViewModel), "UninstallCommand")]
    // AiOfficePage.xaml
    [InlineData(typeof(AiOfficeViewModel), "CancelCommand")]
    [InlineData(typeof(AiOfficeViewModel), "ChooseWorkspaceCommand")]
    [InlineData(typeof(AiOfficeViewModel), "DeleteConversationCommand")]
    [InlineData(typeof(AiOfficeViewModel), "NewConversationCommand")]
    [InlineData(typeof(AiOfficeViewModel), "OpenWorkspaceCommand")]
    [InlineData(typeof(AiOfficeViewModel), "RemoveAttachmentCommand")]
    [InlineData(typeof(AiOfficeViewModel), "RenameConversationCommand")]
    [InlineData(typeof(AiOfficeViewModel), "SendCommand")]
    public void Command_property_exists(Type viewModelType, string propertyName)
    {
        var property = viewModelType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(
            property is not null,
            $"{viewModelType.Name} 上不存在 {propertyName}：XAML 绑定会静默失效（按钮点了没反应）");
    }
}
