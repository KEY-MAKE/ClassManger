using System.IO;
using System.Text.Json;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>AI 办公的本地存储：设置与会话历史（独立于既有 settings.json）。</summary>
public sealed class AiStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;

    /// <param name="baseDirectory">存储目录；传 null 用 <c>%AppData%\ClassManager</c>（测试可注入临时目录）。</param>
    public AiStore(string? baseDirectory = null)
    {
        _dir = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassManager")
            : baseDirectory;
    }

    public string SettingsPath => Path.Combine(_dir, "ai-settings.json");

    public string ConversationsPath => Path.Combine(_dir, "ai-conversations.json");

    /// <summary>读取设置（无文件时返回默认值）。</summary>
    public AiSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(SettingsPath), Options);
                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.WorkspaceDirectory))
                    {
                        settings.WorkspaceDirectory = AiSettings.DefaultWorkspace;
                    }

                    return settings;
                }
            }
        }
        catch
        {
            // 损坏则回落默认
        }

        return new AiSettings { WorkspaceDirectory = AiSettings.DefaultWorkspace };
    }

    /// <summary>保存设置。</summary>
    public void SaveSettings(AiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把可疑字符写成转义片段，便于下次复现时定位（不影响主流程）。</summary>
    private void LogSuspicious(AiConversation conversation, string what, string text)
    {
        try
        {
            Directory.CreateDirectory(_dir);

            var builder = new System.Text.StringBuilder();
            foreach (var c in text.Length > 200 ? text[..200] : text)
            {
                if (c < 32 || c > 126)
                {
                    builder.Append($"\\u{(int)c:x4}");
                }
                else
                {
                    builder.Append(c);
                }
            }

            File.AppendAllText(
                Path.Combine(_dir, "ai-debug.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 会话「{conversation.Title}」{what}含孤立代理项/替换符：{builder}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断日志失败不影响保存
        }
    }

    /// <summary>读取全部会话（按更新时间倒序）。</summary>
    public List<AiConversation> LoadConversations()
    {
        try
        {
            if (!File.Exists(ConversationsPath))
            {
                return new List<AiConversation>();
            }

            var list = JsonSerializer.Deserialize<List<AiConversation>>(
                File.ReadAllText(ConversationsPath), Options);

            return list is null
                ? new List<AiConversation>()
                : list.OrderByDescending(c => c.UpdatedAt).ToList();
        }
        catch
        {
            return new List<AiConversation>();
        }
    }

    /// <summary>最近一次保存失败的原因（成功时为 null）。</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>保存全部会话。</summary>
    public bool SaveConversations(IReadOnlyCollection<AiConversation> conversations)
    {
        try
        {
            // 先消毒：孤立代理项会让 System.Text.Json 抛异常，导致整个文件写不进去
            foreach (var conversation in conversations)
            {
                foreach (var message in conversation.Messages)
                {
                    if (AiTextSanitizer.IsSuspicious(message.Content))
                    {
                        LogSuspicious(conversation, "正文", message.Content);
                    }

                    message.Content = AiTextSanitizer.Clean(message.Content);
                    message.Reasoning = message.Reasoning is null ? null : AiTextSanitizer.Clean(message.Reasoning);
                    message.DisplayContent = message.DisplayContent is null
                        ? null
                        : AiTextSanitizer.Clean(message.DisplayContent);
                }
            }

            Directory.CreateDirectory(_dir);
            File.WriteAllText(
                ConversationsPath,
                JsonSerializer.Serialize(conversations, Options));

            LastSaveError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
            return false;
        }
    }
}
