using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassManager.App.Models;

/// <summary>AI 文件操作权限模式。</summary>
public enum AiPermissionMode
{
    /// <summary>仅工作区（默认）：越界拒绝，写文件前弹确认。</summary>
    WorkspaceOnly = 0,

    /// <summary>全盘无确认：不做路径限制，写入不弹确认（高风险，用户显式开启）。</summary>
    FullDisk = 1,
}

/// <summary>对话中的一条消息。</summary>
public sealed class AiMessage
{
    /// <summary>user / assistant / tool。</summary>
    public string Role { get; set; } = "user";

    /// <summary>正文（回传给模型的内容，可能包含附件正文）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 界面上显示的文本。用户消息带附件时，气泡只显示「你输入的文字 + 附件名」，
    /// 不要把附件正文整段铺出来；历史恢复时优先用它。
    /// </summary>
    public string? DisplayContent { get; set; }

    /// <summary>推理模型的思维链（仅展示，不回传给模型）。</summary>
    public string? Reasoning { get; set; }

    /// <summary>工具结果的 tool_call_id。</summary>
    public string? ToolCallId { get; set; }

    /// <summary>工具名（tool 消息用）。</summary>
    public string? ToolName { get; set; }

    /// <summary>助手消息里的工具调用请求。</summary>
    public List<AiToolCall>? ToolCalls { get; set; }

    /// <summary>是否为错误提示气泡。</summary>
    public bool IsError { get; set; }

    /// <summary>时间。</summary>
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>模型请求的一次工具调用。</summary>
public sealed class AiToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>一个会话（可观察，便于在列表里就地重命名）。</summary>
public sealed partial class AiConversation : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _title = "新对话";

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.Now;

    /// <summary>是否为「就地重命名」状态（不持久化）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isEditing;

    /// <summary>重命名输入框的草稿（不持久化）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _renameDraft = string.Empty;

    [ObservableProperty]
    private List<AiMessage> _messages = new();

    private string _storageKey = string.Empty;

    /// <summary>
    /// 该会话在磁盘上的目录名（时间命名，如 <c>20261030-233045_a1b2</c>）。
    /// 附件与 AI 在本会话里产出的文件都放这个目录，删除会话时整目录一起删。
    /// </summary>
    public string StorageKey
    {
        get => string.IsNullOrWhiteSpace(_storageKey) ? BuildFallbackKey() : _storageKey;
        set => SetProperty(ref _storageKey, value);
    }

    private string BuildFallbackKey()
    {
        var suffix = Id.Length >= 4 ? Id[..4] : Id;
        return $"{UpdatedAt:yyyyMMdd-HHmmss}_{suffix}";
    }

    /// <summary>列表里显示的时间。</summary>
    [JsonIgnore]
    public string UpdatedText => UpdatedAt.ToString("MM-dd HH:mm");

    partial void OnUpdatedAtChanged(DateTime value) => OnPropertyChanged(nameof(UpdatedText));
}

/// <summary>用量累计。</summary>
public sealed class AiUsageTotals
{
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long ReasoningTokens { get; set; }

    public long TotalTokens => PromptTokens + CompletionTokens;

    public void Add(AiUsageTotals other)
    {
        PromptTokens += other.PromptTokens;
        CompletionTokens += other.CompletionTokens;
        ReasoningTokens += other.ReasoningTokens;
    }

    public string Format() =>
        ReasoningTokens > 0
            ? $"输入 {PromptTokens:N0} · 输出 {CompletionTokens:N0}（含思考 {ReasoningTokens:N0}）· 合计 {TotalTokens:N0}"
            : $"输入 {PromptTokens:N0} · 输出 {CompletionTokens:N0} · 合计 {TotalTokens:N0}";
}

/// <summary>用户拖进对话区的附件（已复制进工作区，便于模型用 read_file 读取）。</summary>
public sealed class AiAttachment
{
    /// <summary>原始路径。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>复制到工作区后的绝对路径。</summary>
    public string WorkspaceFullPath { get; init; } = string.Empty;

    /// <summary>工作区内的相对路径（给模型用）。</summary>
    public string WorkspaceRelativePath { get; init; } = string.Empty;

    /// <summary>文件名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>字节数。</summary>
    public long Size { get; init; }

    /// <summary>文本类文件的内联内容（非文本为 null）。</summary>
    public string? TextPreview { get; init; }

    /// <summary>是否成功提取出可交给模型的文本。</summary>
    public bool IsText { get; init; }

    /// <summary>类型标注（文本 / 表格 / 文档 / 图片 / 二进制）。</summary>
    public string KindText { get; init; } = string.Empty;

    /// <summary>大小展示。</summary>
    public string SizeText => Size < 1024
        ? $"{Size} B"
        : Size < 1024 * 1024
            ? $"{Size / 1024.0:0.#} KB"
            : $"{Size / 1024.0 / 1024.0:0.##} MB";

    /// <summary>附件卡片标题。</summary>
    public string DisplayText =>
        string.IsNullOrEmpty(KindText)
            ? $"{Name} · {SizeText}"
            : $"{Name} · {SizeText} · {KindText}";
}

/// <summary>AI 办公的本地设置（独立文件，不动既有 settings.json）。</summary>
public sealed class AiSettings
{
    /// <summary>工作区目录（仅工作区模式下的可访问根）。</summary>
    public string WorkspaceDirectory { get; set; } = string.Empty;

    /// <summary>权限模式。</summary>
    public AiPermissionMode PermissionMode { get; set; } = AiPermissionMode.WorkspaceOnly;

    /// <summary>
    /// 上次选中的对话 Id。
    /// <para>
    /// 少了它就会出现「新建对话 → 切走 → 回来跳到旧对话」：
    /// 每次进页面都重新构造 ViewModel，只能盲选列表第一个。
    /// </para>
    /// </summary>
    public string LastConversationId { get; set; } = string.Empty;

    /// <summary>默认工作区（文档目录下的子文件夹）。</summary>
    public static string DefaultWorkspace => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ClassManager AI");
}
