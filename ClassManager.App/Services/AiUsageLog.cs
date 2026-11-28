using System.IO;
using System.Text;

namespace ClassManager.App.Services;

/// <summary>
/// AI 调用流水账。
/// <para>
/// <b>为什么要有这个</b>：用户反馈「没用 AI 办公，key 却在掉」，但程序里没有任何记录，
/// 只能靠猜。有了这行日志，每次请求的时间、模型、token 用量、成败、耗时都留痕，
/// 下次再问「谁在调」，直接看文件就行。
/// </para>
/// <para>文件：<c>%AppData%\ClassManager\ai-usage.log</c>（按行追加，用记事本就能看）</para>
/// </summary>
public static class AiUsageLog
{
    private static readonly object Gate = new();

    /// <summary>日志路径。</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClassManager",
        "ai-usage.log");

    /// <summary>
    /// 记一笔。任何异常都吞掉 —— 记账失败不能影响主流程。
    /// </summary>
    /// <param name="ok">这一笔是成功还是失败。</param>
    /// <param name="model">实际用的模型。</param>
    /// <param name="keyLabel">用的哪把 key（api1~api6）。</param>
    /// <param name="elapsed">耗时。</param>
    /// <param name="promptTokens">输入 token（拿不到就 0）。</param>
    /// <param name="completionTokens">输出 token（拿不到就 0）。</param>
    /// <param name="note">补充说明（失败原因等）。</param>
    public static void Write(
        bool ok,
        string model,
        string keyLabel,
        TimeSpan elapsed,
        long promptTokens,
        long completionTokens,
        string? note = null)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append(" | ").Append(ok ? "OK  " : "FAIL")
                .Append(" | ").Append(string.IsNullOrWhiteSpace(model) ? "?" : model)
                .Append(" | ").Append(string.IsNullOrWhiteSpace(keyLabel) ? "?" : keyLabel)
                .Append(" | in=").Append(promptTokens)
                .Append(" out=").Append(completionTokens)
                .Append(" | ").Append(elapsed.TotalSeconds.ToString("F1")).Append('s');

            if (!string.IsNullOrWhiteSpace(note))
            {
                var trimmed = note.Length > 120 ? note[..120] : note;
                line.Append(" | ").Append(trimmed.Replace('\n', ' ').Replace('\r', ' '));
            }

            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);

                // 超过 2MB 就轮转一次，避免长期跑无限大
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                {
                    var backup = LogPath + ".old";
                    File.Copy(LogPath, backup, overwrite: true);
                    File.Delete(LogPath);
                }

                File.AppendAllText(LogPath, line.Append('\n').ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // 记账失败不影响主流程
        }
    }
}
