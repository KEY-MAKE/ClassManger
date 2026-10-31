using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ClassManager.App.Models;
using MiniExcelLibs;

namespace ClassManager.App.Services;

/// <summary>
/// 座位表导入：只接受本程序导出的 xlsx（带「版式参数」表与格式标识），
/// 先按参数还原行列数、逐道行/列间距、表头开关，再回填每个座位的姓名。
/// </summary>
public static class SeatSheetImporter
{
    /// <summary>尝试导入。成功时 <paramref name="layout"/> 已被完整覆盖。</summary>
    public static bool TryImport(string filePath, SeatLayout layout, out string message)
    {
        message = string.Empty;

        try
        {
            if (!File.Exists(filePath))
            {
                message = "文件不存在。";
                return false;
            }

            var sheetNames = MiniExcel.GetSheetNames(filePath).ToList();

            if (!sheetNames.Contains(SeatSheetFormat.ParamSheetName)
                || !sheetNames.Contains(SeatSheetFormat.SeatSheetName))
            {
                message =
                    $"这不是本程序导出的座位表：缺少「{SeatSheetFormat.ParamSheetName}」或「{SeatSheetFormat.SeatSheetName}」工作表。\n" +
                    "请在座位管理页用「导出为 Excel」生成文件后再修改/导入。";
                return false;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in ReadRows(filePath, SeatSheetFormat.ParamSheetName))
            {
                var key = Get(row, SeatSheetFormat.ParamKeyHeader);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                parameters[key.Trim()] = Get(row, SeatSheetFormat.ParamValueHeader) ?? string.Empty;
            }

            if (!parameters.TryGetValue("格式标识", out var marker)
                || !string.Equals(marker, SeatSheetFormat.Marker, StringComparison.Ordinal))
            {
                message = $"格式标识不匹配，拒绝导入（期望 {SeatSheetFormat.Marker}）。";
                return false;
            }

            if (!int.TryParse(GetValue(parameters, "行数"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows)
                || !int.TryParse(GetValue(parameters, "列数"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cols))
            {
                message = "版式参数缺少「行数」或「列数」。";
                return false;
            }

            if (rows is < 1 or > 20 || cols is < 1 or > 20)
            {
                message = $"行列数超出允许范围：{rows} × {cols}（允许 1~20）。";
                return false;
            }

            layout.ClearAll();
            layout.Resize(rows, cols);

            for (var i = 0; i < cols - 1; i++)
            {
                if (TryParseDouble(GetValue(parameters, $"列间距_{i + 1}"), out var gap))
                {
                    layout.SetColumnGap(i, gap);
                }
            }

            for (var i = 0; i < rows - 1; i++)
            {
                if (TryParseDouble(GetValue(parameters, $"行间距_{i + 1}"), out var gap))
                {
                    layout.SetRowGap(i, gap);
                }
            }

            var headerValue = GetValue(parameters, "显示表头").Trim();
            layout.ShowHeaders = headerValue is "1" or "true" or "True";

            var placed = 0;

            // 讲台两侧的额外座位（不在网格里，用伪坐标 row = -1 存）
            var platformLeft = GetValue(parameters, "讲台左侧位").Trim();
            if (platformLeft.Length > 0)
            {
                layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatLeftCol, platformLeft);
                placed++;
            }

            var platformRight = GetValue(parameters, "讲台右侧位").Trim();
            if (platformRight.Length > 0)
            {
                layout.Set(SeatLayout.PlatformSeatRow, SeatLayout.PlatformSeatRightCol, platformRight);
                placed++;
            }
            foreach (var row in ReadRows(filePath, SeatSheetFormat.SeatSheetName))
            {
                var rowIndex = -1;

                // 行标签列：表头是「行\列」，值是「第N行」
                foreach (var pair in row)
                {
                    if (!pair.Key.Contains('\\'))
                    {
                        continue;
                    }

                    rowIndex = ParseIndex(pair.Value?.ToString(), "行");
                    break;
                }

                if (rowIndex < 0 || rowIndex >= rows)
                {
                    continue;
                }

                foreach (var pair in row)
                {
                    var colIndex = ParseIndex(pair.Key, "列");
                    if (colIndex < 0 || colIndex >= cols)
                    {
                        continue;
                    }

                    var name = pair.Value?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        layout.Set(rowIndex, colIndex, name);
                        placed++;
                    }
                }
            }

            message = $"已导入 {rows} 行 × {cols} 列，{placed} 个座位已安排。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"读取失败：{ex.Message}";
            return false;
        }
    }

    private static List<IDictionary<string, object>> ReadRows(string path, string sheetName)
    {
        var list = new List<IDictionary<string, object>>();

        foreach (var row in MiniExcel.Query(path, useHeaderRow: true, excelType: ExcelType.XLSX, sheetName: sheetName))
        {
            if (row is IDictionary<string, object> dictionary)
            {
                list.Add(dictionary);
            }
        }

        return list;
    }

    private static string? Get(IDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string GetValue(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : string.Empty;

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    /// <summary>把「第N行 / 第N列 / 纯数字」解析为 0 基索引；失败返回 -1。</summary>
    private static int ParseIndex(string? text, string suffix)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }

        var match = Regex.Match(text, @"第\s*(\d+)\s*" + suffix);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numbered))
        {
            return numbered - 1;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain))
        {
            return plain - 1;
        }

        return -1;
    }
}
