using System.IO;
using System.IO.Compression;
using System.Xml;

namespace ClassManager.App.Services;

/// <summary>
/// 检测 xlsx 里被「筛选」隐藏掉的行。
/// <para>
/// 需求背景：老师给的成绩单常常是「全校表 + 筛选出本班」。用 Excel 的筛选功能时，
/// 被筛掉的行**仍然是文件里的数据行**，只是 <c>hidden="1"</c>。
/// MiniExcel 读出来的是全部行 —— 于是导入名单变成了全校几百人。
/// </para>
/// <para>
/// 这里直接拆 xlsx（本质是 zip + xml），读 <c>xl/worksheets/sheetN.xml</c> 里
/// 每个 <c>&lt;row&gt;</c> 的 <c>r</c>（行号）和 <c>hidden</c> 属性，
/// 拿到「第几行是隐藏的」，供上层按顺序剔除。
/// </para>
/// </summary>
public static class XlsxRowFilter
{
    /// <summary>一行在表里的位置与可见性。</summary>
    public sealed record RowInfo(int RowNumber, bool Hidden);

    /// <summary>
    /// 表的结构：表头行号 + 数据行（按文件中的先后顺序）。
    /// <para>
    /// 顺序很关键 —— MiniExcel 返回的行列表也是按文件顺序，
    /// 两边数量一致时就能按索引一一对应。
    /// </para>
    /// </summary>
    public sealed record SheetLayout(int HeaderRow, IReadOnlyList<RowInfo> DataRows)
    {
        /// <summary>被隐藏（筛掉）的数据行数。</summary>
        public int HiddenCount => DataRows.Count(r => r.Hidden);
    }

    /// <summary>
    /// 分析第一张工作表的行可见性。
    /// <para>非 xlsx、文件损坏、结构异常时返回 <c>null</c>（调用方按「不启用过滤」处理）。</para>
    /// </summary>
    public static SheetLayout? Analyze(string xlsxPath)
    {
        try
        {
            if (!File.Exists(xlsxPath)
                || !string.Equals(Path.GetExtension(xlsxPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var archive = ZipFile.OpenRead(xlsxPath);
            var sheet = archive.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (sheet is null)
            {
                return null;
            }

            var rows = ReadRows(sheet);

            if (rows.Count < 2)
            {
                // 只有表头或什么都没有，谈不上筛选
                return null;
            }

            // 第一行当表头（与 MiniExcel 的 useHeaderRow: true 一致）
            var header = rows[0].RowNumber;
            var dataRows = rows.Skip(1).ToList();

            return new SheetLayout(header, dataRows);
        }
        catch
        {
            // 读不出来就当作「没有筛选信息」，让导入走原来的路径，绝不能因此让导入失败
            return null;
        }
    }

    private static List<RowInfo> ReadRows(ZipArchiveEntry sheet)
    {
        var rows = new List<RowInfo>();

        using var stream = sheet.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
        });

        var fallbackRowNumber = 1;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "row", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // r 缺省时按出现顺序推
            var rAttr = reader.GetAttribute("r");
            var rowNumber = int.TryParse(rAttr, out var parsed) ? parsed : fallbackRowNumber;

            fallbackRowNumber = rowNumber + 1;

            // hidden="1" / hidden="true" 都算隐藏
            var hiddenAttr = reader.GetAttribute("hidden");
            var hidden = hiddenAttr is "1"
                         || string.Equals(hiddenAttr, "true", StringComparison.OrdinalIgnoreCase);

            rows.Add(new RowInfo(rowNumber, hidden));
        }

        return rows;
    }
}
