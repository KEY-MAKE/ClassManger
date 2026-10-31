namespace ClassManager.App.Services;

/// <summary>
/// 座位表 xlsx 的格式约定。
/// 导出时把版式参数（行列数、每一道行/列间距、是否显示表头）一起写进「版式参数」工作表，
/// 导入时据此完整还原排版，做到「导出的文件能原样读回来」。
/// </summary>
public static class SeatSheetFormat
{
    /// <summary>格式标识，用于拒绝来自其它来源的 xlsx。</summary>
    public const string Marker = "ClassManager.SeatLayout/v1";

    /// <summary>版式参数工作表名。</summary>
    public const string ParamSheetName = "版式参数";

    /// <summary>座位表工作表名。</summary>
    public const string SeatSheetName = "座位表";

    /// <summary>统计信息工作表名。</summary>
    public const string InfoSheetName = "统计信息";

    /// <summary>参数表中的两列。</summary>
    public const string ParamKeyHeader = "参数";

    /// <summary>参数表中的值列。</summary>
    public const string ParamValueHeader = "值";
}
