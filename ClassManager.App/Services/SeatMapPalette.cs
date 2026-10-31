using System.Windows.Media;

namespace ClassManager.App.Services;

/// <summary>
/// 座位画布 / 导出所用的固定配色（不随明暗主题变化）。
/// 画布为「蓝白纸面」风；导出 / 打印为「白底黑格」风。
/// </summary>
public static class SeatMapPalette
{
    // ---------------- 画布（蓝白） ----------------

    /// <summary>画布底：纯白。</summary>
    public static readonly Color CanvasBackground = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>座位边框（浅蓝）。</summary>
    public static readonly Color SeatBorder = Color.FromRgb(0x9C, 0xC3, 0xE5);

    /// <summary>空座位填充（白）。</summary>
    public static readonly Color SeatEmptyFill = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>已排座填充（浅蓝）。</summary>
    public static readonly Color SeatOccupiedFill = Color.FromRgb(0xD6, 0xE9, 0xFA);

    /// <summary>选中座位填充。</summary>
    public static readonly Color SeatSelectedFill = Color.FromRgb(0xCF, 0xE4, 0xF7);

    /// <summary>强调蓝。</summary>
    public static readonly Color AccentBlue = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>座位内姓名文字。</summary>
    public static readonly Color NameText = Color.FromRgb(0x1A, 0x1A, 0x1A);

    /// <summary>排标签 / 分组表头文字。</summary>
    public static readonly Color LabelText = Color.FromRgb(0x4A, 0x6B, 0x85);

    /// <summary>讲台填充（白）。</summary>
    public static readonly Color PlatformFill = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>讲台边框（黑）。</summary>
    public static readonly Color PlatformBorder = Color.FromRgb(0x00, 0x00, 0x00);

    /// <summary>讲台文字（黑）。</summary>
    public static readonly Color PlatformText = Color.FromRgb(0x00, 0x00, 0x00);

    // ---------------- 导出 / 打印（白底黑格） ----------------

    /// <summary>导出底：纯白。</summary>
    public static readonly Color ExportBackground = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>导出线色 / 文字：纯黑。</summary>
    public static readonly Color ExportInk = Color.FromRgb(0x00, 0x00, 0x00);
}
