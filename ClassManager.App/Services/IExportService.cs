using System.Windows;
using System.Windows.Media;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>导出服务接口（xlsx / 打印风 PNG / 矢量座位表）。</summary>
public interface IExportService
{
    /// <summary>导出座位表为 xlsx（双 sheet：座位表 / 统计信息）；座位单元格只写学生姓名。</summary>
    void ExportSeatXlsx(
        string filePath,
        SeatLayout layout,
        string className,
        DateTime generatedAt);

    /// <summary>
    /// 构建「打印风」（白底黑格）座位表矢量图，供导出 PNG 与打印共用。
    /// 不绘制「座位表」大标题与生成时间，只画网格、讲台与可选表头。
    /// </summary>
    /// <param name="layout">座位布局。</param>
    /// <param name="className">班级名称（仅当 <paramref name="includeClassName"/> 为 true 时才绘制）。</param>
    /// <param name="includeHeaders">是否包含列表头（第x列）与排标签（第x排）。</param>
    /// <param name="includeClassName">是否绘制班级名称。</param>
    /// <returns>矢量视觉对象与其逻辑尺寸（96 DPI 坐标）。</returns>
    (DrawingVisual Visual, Size Size) BuildSeatSheet(
        SeatLayout layout,
        string className,
        bool includeHeaders,
        bool includeClassName);

    /// <summary>
    /// 导出座位表为图片（白底黑格，96 DPI × 2 缩放）。
    /// </summary>
    void ExportSeatImage(
        string filePath,
        SeatLayout layout,
        string className,
        bool includeHeaders,
        bool includeClassName,
        bool jpeg = false);
}
