using System.Text;

namespace ClassManager.App.Services;

/// <summary>
/// 文本消毒：清理流式响应里可能出现的孤立代理项（半个 surrogate pair）、
/// U+FFFD 替换符与除 \n\r\t 之外的控制字符。
/// <para>
/// 为什么需要：流式分片有可能把 4 字节字符（emoji / 扩展区汉字）拆成两个 delta，
/// 拼接后留下孤立代理项——界面显示为乱码，而且 <c>System.Text.Json</c> 序列化时会直接抛异常，
/// 导致整次会话保存失败（表现为「用户消息还在、AI 回复丢了」）。
/// </para>
/// <para>
/// 注意：**只能对最终完整文本调用**，不要对每个 delta 单独调用，否则会把正常的代理对拆断。
/// </para>
/// </summary>
public static class AiTextSanitizer
{
    /// <summary>清理文本。</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var changed = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(c).Append(value[++i]);
                }
                else
                {
                    changed = true; // 孤立的高代理项：丢弃
                }

                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                changed = true; // 孤立的低代理项：丢弃
                continue;
            }

            if (c == '\uFFFD')
            {
                changed = true;
                continue;
            }

            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
            {
                changed = true;
                continue;
            }

            builder.Append(c);
        }

        return changed ? builder.ToString() : value;
    }

    /// <summary>该文本是否含孤立代理项或替换符（用于日志诊断）。</summary>
    public static bool IsSuspicious(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
                continue;
            }

            if (char.IsLowSurrogate(c) || c == '\uFFFD')
            {
                return true;
            }
        }

        return false;
    }
}
