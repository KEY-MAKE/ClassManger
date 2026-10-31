using System;
using System.IO;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// AI 会话持久化契约：重启后对话必须还在；界面显示文本与回传内容要分开存；
/// 重命名的临时状态不能写进文件。
/// </summary>
public class AiStoreRoundTripTests : IDisposable
{
    private readonly string _dir;

    public AiStoreRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cm_aistore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Conversations_survive_round_trip_and_keep_display_text()
    {
        var store = new AiStore(_dir);

        var first = new AiConversation { Title = "数学复习" };
        first.Messages.Add(new AiMessage
        {
            Role = "user",
            // 回传给模型的完整内容（含附件正文）
            Content = "看看这个文件\n\n【附件】…一整段文件正文…",
            // 气泡上只显示这一行
            DisplayContent = "看看这个文件\n\n📎 笔记.md",
        });
        first.Messages.Add(new AiMessage { Role = "assistant", Content = "好的", Reasoning = "思考中" });

        var second = new AiConversation { Title = "新对话" };

        store.SaveConversations(new[] { first, second });

        var json = File.ReadAllText(store.ConversationsPath);
        Assert.DoesNotContain("IsEditing", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameDraft", json, StringComparison.Ordinal);
        Assert.Contains("DisplayContent", json, StringComparison.Ordinal);

        var loaded = store.LoadConversations();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, c => c.Title == "数学复习");
        Assert.Contains(loaded, c => c.Title == "新对话");

        var restored = loaded.First(c => c.Title == "数学复习");
        Assert.Equal(2, restored.Messages.Count);
        Assert.Equal("看看这个文件\n\n📎 笔记.md", restored.Messages[0].DisplayContent);
        Assert.Contains("一整段文件正文", restored.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("思考中", restored.Messages[1].Reasoning);
    }

    [Fact]
    public void Settings_round_trip_keeps_permission_and_workspace()
    {
        var store = new AiStore(_dir);

        store.SaveSettings(new AiSettings
        {
            WorkspaceDirectory = @"D:\AI工作区",
            PermissionMode = AiPermissionMode.FullDisk,
        });

        var loaded = store.LoadSettings();

        Assert.Equal(@"D:\AI工作区", loaded.WorkspaceDirectory);
        Assert.Equal(AiPermissionMode.FullDisk, loaded.PermissionMode);
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var store = new AiStore(Path.Combine(_dir, "empty"));

        Assert.Empty(store.LoadConversations());
        Assert.Equal(AiSettings.DefaultWorkspace, store.LoadSettings().WorkspaceDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch
        {
            // 清理失败忽略
        }
    }
}
