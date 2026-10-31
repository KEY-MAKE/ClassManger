using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassManager.App.Services;

/// <summary>
/// AI 附件的统一读取入口。
/// <para>
/// 之前的问题：docx 是 OOXML（zip），模型或工具直接按文本读时会读到内嵌图片的二进制数据，
/// 表现为一整屏乱码，模型随即放弃任务。
/// </para>
/// 这里按类型分流，保证交给模型的内容**只有文本**：
/// 文本类直接读；xlsx 用 MiniExcel 渲染；docx / pptx 解压后只抽文字节点；
/// 图片与不支持的二进制一律明确告知，绝不把字节流塞给模型。
/// </summary>
public static class AiDocumentReader
{
    /// <summary>交给模型的最大字符数。</summary>
    public const int MaxChars = 20000;

    /// <summary>读取结果的种类。</summary>
    public enum Kind
    {
        /// <summary>纯文本 / 代码 / 标记语言。</summary>
        Text,

        /// <summary>表格（xlsx / xls）。</summary>
        Spreadsheet,

        /// <summary>文档（docx / pptx）。</summary>
        Document,

        /// <summary>图片（当前模型不支持读图）。</summary>
        Image,

        /// <summary>无法按文本读取的二进制。</summary>
        Binary,

        /// <summary>文件不存在。</summary>
        Missing,
    }

    /// <summary>一次读取的结果。</summary>
    public sealed class Outcome
    {
        public Kind Kind { get; init; }

        /// <summary>是否成功取到了可交给模型的内容。</summary>
        public bool Ok => Kind is Kind.Text or Kind.Spreadsheet or Kind.Document;

        /// <summary>交给模型的文本（不成功时是给模型的说明）。</summary>
        public string Text { get; init; } = string.Empty;
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".log", ".ini", ".cfg", ".conf", ".config",
        ".csv", ".tsv", ".json", ".jsonc", ".xml", ".xaml", ".yml", ".yaml", ".toml",
        ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".java", ".kt", ".go", ".rb", ".php",
        ".c", ".h", ".cpp", ".hpp", ".rs", ".swift", ".sql", ".sh", ".bash", ".ps1", ".bat",
        ".html", ".htm", ".css", ".scss", ".less", ".vue", ".svelte",
        ".mermaid", ".editorconfig", ".gitignore", ".gitattributes", ".env", ".properties",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico", ".heic", ".svg",
    };

    /// <summary>读取文件，返回可安全交给模型的文本。</summary>
    public static Outcome Read(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new Outcome { Kind = Kind.Missing, Text = $"文件不存在：{path}" };
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();

            if (extension is ".docx")
            {
                return ReadDocx(path);
            }

            if (extension is ".pptx")
            {
                return ReadPptx(path);
            }

            if (extension is ".xlsx" or ".xls" or ".xlsm")
            {
                return ReadSpreadsheet(path);
            }

            if (ImageExtensions.Contains(extension))
            {
                return new Outcome
                {
                    Kind = Kind.Image,
                    Text = $"（{Path.GetFileName(path)} 是图片文件，当前模型只支持文本输入，无法识别图片内容。"
                           + "如果需要处理图片里的内容，请把文字部分复制给我。）",
                };
            }

            if (TextExtensions.Contains(extension))
            {
                return ReadText(path);
            }

            // 未知类型：先看能不能当文本读，不能就明确拒绝，绝不返回原始字节
            var bytes = File.ReadAllBytes(path);
            if (LooksLikeBinary(bytes))
            {
                return new Outcome
                {
                    Kind = Kind.Binary,
                    Text = $"（{Path.GetFileName(path)} 是二进制文件（{bytes.Length:N0} 字节），无法按文本读取。"
                           + "支持的类型：文本/代码/CSV/JSON、Excel（xlsx/xls）、Word（docx）、PPT（pptx）。）",
                };
            }

            return ReadText(path);
        }
        catch (Exception ex)
        {
            return new Outcome
            {
                Kind = Kind.Binary,
                Text = $"（读取 {Path.GetFileName(path)} 失败：{ex.Message}）",
            };
        }
    }

    private static Outcome ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (LooksLikeBinary(bytes))
        {
            return new Outcome
            {
                Kind = Kind.Binary,
                Text = $"（{Path.GetFileName(path)} 看似文本但内含二进制数据，已拒绝按文本读取，避免输出乱码。）",
            };
        }

        var text = File.ReadAllText(path);

        // 去掉除换行/制表外的控制字符
        text = Regex.Replace(text, @"[\p{C}&&[^\r\n\t]]", string.Empty);

        if (text.Length > MaxChars)
        {
            text = text[..MaxChars] + "\n…（内容过长，已截断）";
        }

        return new Outcome { Kind = Kind.Text, Text = text };
    }

    private static Outcome ReadSpreadsheet(string path)
    {
        var builder = new StringBuilder();

        try
        {
            var sheetNames = MiniExcelLibs.MiniExcel.GetSheetNames(path).ToList();

            foreach (var sheetName in sheetNames.Take(5))
            {
                builder.AppendLine($"【工作表：{sheetName}】");

                var rowIndex = 0;
                var columns = new List<string>();

                foreach (var row in MiniExcelLibs.MiniExcel.Query(
                             path,
                             useHeaderRow: true,
                             excelType: MiniExcelLibs.ExcelType.XLSX,
                             sheetName: sheetName))
                {
                    if (rowIndex >= 300)
                    {
                        builder.AppendLine("…（表格过长，仅取前 300 行）");
                        break;
                    }

                    if (row is not IDictionary<string, object> map)
                    {
                        continue;
                    }

                    if (rowIndex == 0)
                    {
                        columns = map.Keys.ToList();
                        builder.AppendLine(string.Join(", ", columns));
                    }

                    builder.AppendLine(string.Join(
                        ", ",
                        columns.Count > 0
                            ? columns.Select(c => FormatCell(map.TryGetValue(c, out var v) ? v : null))
                            : map.Values.Select(FormatCell)));

                    rowIndex++;

                    if (builder.Length > MaxChars)
                    {
                        break;
                    }
                }

                builder.AppendLine();

                if (builder.Length > MaxChars)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            return new Outcome
            {
                Kind = Kind.Binary,
                Text = $"（读取表格 {Path.GetFileName(path)} 失败：{ex.Message}）",
            };
        }

        var text = builder.ToString();
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars] + "\n…（内容过长，已截断）";
        }

        return new Outcome { Kind = Kind.Spreadsheet, Text = text };
    }

    private static Outcome ReadDocx(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");

            if (entry is null)
            {
                // 可能是老式 .doc（OLE 二进制），明确拒绝
                return new Outcome
                {
                    Kind = Kind.Binary,
                    Text = $"（{Path.GetFileName(path)} 不是 OOXML 的 docx（可能是老版 .doc），无法按文本读取。请另存为 .docx。）",
                };
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();

            var builder = new StringBuilder();

            // 按段落切分，只取 <w:t> 里的文字，彻底跳过内嵌图片等二进制
            var paragraphs = xml.Split("</w:p>", StringSplitOptions.RemoveEmptyEntries);
            foreach (var paragraph in paragraphs)
            {
                var line = new StringBuilder();
                // 标签名必须是 w:t 本身：<w:t[^>]*> 会误匹配 <w:tbl>/<w:tr>/<w:tc> 等，
                // 导致把整段 XML 当成正文捕获出来（真实 docx 上已复现）
                foreach (Match match in Regex.Matches(paragraph, @"<w:t(?:\s[^>]*)?>(.*?)</w:t>", RegexOptions.Singleline))
                {
                    line.Append(WebUtility.HtmlDecode(match.Groups[1].Value));
                }

                var text = StripTags(line.ToString()).Trim();
                if (text.Length > 0)
                {
                    builder.AppendLine(text);
                }

                if (builder.Length > MaxChars)
                {
                    break;
                }
            }

            var result = StripTags(builder.ToString());
            if (result.Length > MaxChars)
            {
                result = result[..MaxChars] + "\n…（内容过长，已截断）";
            }

            return new Outcome
            {
                Kind = Kind.Document,
                Text = result.Trim().Length == 0
                    ? $"（{Path.GetFileName(path)} 里没有可提取的文字。）"
                    : result,
            };
        }
        catch (Exception ex)
        {
            return new Outcome
            {
                Kind = Kind.Binary,
                Text = $"（读取 {Path.GetFileName(path)} 失败：{ex.Message}）",
            };
        }
    }

    private static Outcome ReadPptx(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            var slideEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToList();

            if (slideEntries.Count == 0)
            {
                return new Outcome
                {
                    Kind = Kind.Binary,
                    Text = $"（{Path.GetFileName(path)} 里没有找到幻灯片内容。）",
                };
            }

            var builder = new StringBuilder();
            var index = 1;

            foreach (var entry in slideEntries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var xml = reader.ReadToEnd();

                var line = new StringBuilder();
                foreach (Match match in Regex.Matches(xml, @"<a:t(?:\s[^>]*)?>(.*?)</a:t>", RegexOptions.Singleline))
                {
                    line.Append(WebUtility.HtmlDecode(match.Groups[1].Value)).Append(' ');
                }

                var text = line.ToString().Trim();
                if (text.Length > 0)
                {
                    builder.AppendLine($"第 {index} 页：{text}");
                }

                index++;

                if (builder.Length > MaxChars)
                {
                    break;
                }
            }

            var result = builder.ToString();
            if (result.Length > MaxChars)
            {
                result = result[..MaxChars] + "\n…（内容过长，已截断）";
            }

            return new Outcome { Kind = Kind.Document, Text = result.Trim().Length == 0 ? $"（{Path.GetFileName(path)} 里没有可提取的文字。）" : result };
        }
        catch (Exception ex)
        {
            return new Outcome
            {
                Kind = Kind.Binary,
                Text = $"（读取 {Path.GetFileName(path)} 失败：{ex.Message}）",
            };
        }
    }

    /// <summary>兜底：剥掉任何残余的 XML/HTML 标签，保证交给模型的只有文字。</summary>
    private static string StripTags(string text) =>
        string.IsNullOrEmpty(text) ? text : Regex.Replace(text, "<[^>]+>", string.Empty);

    private static string FormatCell(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is DateTime dt)
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Contains(',') || text.Contains('"') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    /// <summary>
    /// 判断字节流是否像二进制。
    /// <para>
    /// 注意：**不能**用「0x80-0x9F 区间的字节算可疑」这种朴素判断——UTF-8 中文的连续字节
    /// 大多落在这个区间（例如「这」= E8 BF 99 里的 0x99），会把所有中文文本文件误判成二进制。
    /// </para>
    /// 这里改为：含 NUL → 二进制；否则尝试严格 UTF-8 解码，解不了 → 二进制；
    /// 最后再看真实控制字符（不含 \t \r \n）的占比。
    /// </summary>
    private static bool LooksLikeBinary(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var probeLength = Math.Min(bytes.Length, 8192);

        for (var i = 0; i < probeLength; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        try
        {
            // 严格解码：合法的 UTF-8（含中文、emoji）都能通过，随机字节基本会抛异常
            var strict = new UTF8Encoding(false, true);
            _ = strict.GetCharCount(bytes);
        }
        catch (DecoderFallbackException)
        {
            return true;
        }

        var suspicious = 0;
        for (var i = 0; i < probeLength; i++)
        {
            var b = bytes[i];
            if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
            {
                suspicious++;
            }
        }

        return suspicious > probeLength * 0.15;
    }
}
