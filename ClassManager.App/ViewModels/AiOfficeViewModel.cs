using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using ClassManager.App.Models;
using ClassManager.App.Services;
using ClassManager.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;
using Microsoft.Win32;

namespace ClassManager.App.ViewModels;

/// <summary>聊天气泡（UI 专用，支持流式增量刷新）。</summary>
public sealed partial class ChatItem : ObservableObject
{
    [ObservableProperty]
    private string _role = "assistant";

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _reasoning = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    public bool IsUser => Role == "user";

    public bool IsAssistant => Role == "assistant";

    public bool IsTool => Role == "tool";

    public bool IsError => Role == "error";

    /// <summary>消息前方的角色标签（取代气泡）。</summary>
    public string RoleLabel => Role switch
    {
        "user" => "你",
        "tool" => "工具",
        "error" => "错误",
        _ => "AI",
    };

    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    /// <summary>思考过程、工具调用等"过程"步骤（默认折叠，只留一个三角标）。</summary>
    public ObservableCollection<string> Steps { get; } = new();

    /// <summary>是否有可展开的过程内容。</summary>
    public bool HasProcess => HasReasoning || Steps.Count > 0;

    /// <summary>
    /// 「过程」是否展开。放在数据里而不是只写在 XAML 模板上：
    /// 容器复用会把上一次的展开状态带回来，导致重新进入页面时过程被摊开。
    /// </summary>
    [ObservableProperty]
    private bool _isProcessExpanded;

    /// <summary>折叠时的摘要文字。</summary>
    public string ProcessSummary
    {
        get
        {
            var parts = new List<string>();
            if (HasReasoning)
            {
                parts.Add($"思考 {Reasoning.Length} 字");
            }

            if (Steps.Count > 0)
            {
                parts.Add($"过程 {Steps.Count} 步");
            }

            return parts.Count == 0 ? "过程" : string.Join(" · ", parts);
        }
    }

    public string TimeText { get; init; } = DateTime.Now.ToString("HH:mm");

    public ChatItem()
    {
        Steps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProcess));
            OnPropertyChanged(nameof(ProcessSummary));
        };
    }

    partial void OnRoleChanged(string value)
    {
        OnPropertyChanged(nameof(IsUser));
        OnPropertyChanged(nameof(IsAssistant));
        OnPropertyChanged(nameof(IsTool));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(RoleLabel));
    }

    partial void OnReasoningChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(HasProcess));
        OnPropertyChanged(nameof(ProcessSummary));
    }
}

/// <summary>AI 办公页 ViewModel：多会话、流式对话、本地文件工具（工作区沙箱 + 写前确认）。</summary>
public sealed partial class AiOfficeViewModel : ObservableObject
{
    private const int MaxToolRounds = 12;

    /// <summary>
    /// 轮次用尽时允许自动续跑的次数。上一轮还在调工具说明任务没干完，
    /// 不该在这里硬生生截断——这正是「莫名其妙报任务完成」的另一个来源。
    /// </summary>
    private const int MaxAutoContinuations = 2;

    /// <summary>
    /// 连续异常（输出被截断 / DSML 泄漏）的最大纠错重试次数。
    /// </summary>
    private const int MaxAnomalyRetries = 2;



    private readonly IShell _shell;
    private readonly IAiChatService _chat;
    private readonly AiStore _store;

    private AiSettings _settings;
    private AiFileTools _tools;
    private List<AiMessage> _history = new();
    private CancellationTokenSource? _cts;
    private bool _writesApprovedForSession;
    private readonly object _persistLock = new();

    public AiOfficeViewModel(IShell shell)
    {
        _shell = shell;
        _chat = AppServices.Ai;
        _store = AppServices.AiStore;

        _settings = _store.LoadSettings();
        _tools = new AiFileTools(_settings, ConfirmWriteAsync);

        WorkspacePath = _settings.WorkspaceDirectory;
        IsFullDisk = _settings.PermissionMode == AiPermissionMode.FullDisk;

        // 把内置的《试卷格式规范.md》放到工作区，AI 用 read_file 就能读到，
        // 用户不用每次上传 —— 这是「按规范改试卷」的前提。
        EnsureExamSpecInWorkspace(WorkspacePath);

        // 按最近使用排序：文件的物理顺序不代表新旧，之前直接取 [0] 会选中旧会话
        var restored = _store.LoadConversations()
            .OrderByDescending(c => c.UpdatedAt)
            .ToList();

        foreach (var conversation in restored)
        {
            Conversations.Add(conversation);
        }

        // 优先恢复「上次真正停留的那个会话」，而不是列表第一个
        var last = restored.FirstOrDefault(c => c.Id == _settings.LastConversationId);

        if (last is not null)
        {
            SelectedConversation = last;
        }
        else if (Conversations.Count > 0)
        {
            SelectedConversation = Conversations[0];
        }
        else
        {
            NewConversation();
        }

        UpdateStatusTexts();
    }

    /// <summary>内置试卷规范在工作区里的文件名（用户看到的名字）。</summary>
    private const string ExamSpecFileName = "试卷格式规范.md";

    /// <summary>
    /// 把嵌入的《试卷格式规范.md》释放到工作区。
    /// <para>已存在就不覆盖 —— 用户可能按自己的习惯改过。</para>
    /// </summary>
    private static void EnsureExamSpecInWorkspace(string workspace)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return;
            }

            Directory.CreateDirectory(workspace);

            var target = Path.Combine(workspace, ExamSpecFileName);

            if (File.Exists(target))
            {
                return;
            }

            var assembly = typeof(AiOfficeViewModel).Assembly;

            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ai.exam-spec.md", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                return;
            }

            using var source = assembly.GetManifestResourceStream(resourceName);

            if (source is null)
            {
                return;
            }

            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
        catch
        {
            // 释放失败不影响 AI 办公本身，模型会被告知「工作区没有这个文件」
        }
    }

    /// <summary>会话列表。</summary>
    public ObservableCollection<AiConversation> Conversations { get; } = new();

    /// <summary>当前会话的消息气泡。</summary>
    public ObservableCollection<ChatItem> Messages { get; } = new();

    /// <summary>待发送的附件（拖进对话区的文件）。</summary>
    public ObservableCollection<AiAttachment> Attachments { get; } = new();

    /// <summary>可按文本内联读取的扩展名。</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".yaml", ".yml",
        ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css",
        ".log", ".ini", ".bat", ".ps1", ".sh", ".sql", ".java", ".cpp", ".c", ".h",
        ".mermaid", ".config", ".editorconfig",
    };

    private const int MaxInlineChars = 20000;

    [ObservableProperty]
    private AiConversation? _selectedConversation;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;


    [ObservableProperty]
    private string _workspacePath = string.Empty;

    [ObservableProperty]
    private bool _isFullDisk;

    [ObservableProperty]
    private string _usageText = string.Empty;

    [ObservableProperty]
    private string _permissionText = string.Empty;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>发送按钮可用性（有文字或有附件即可发送）。</summary>
    public bool CanSend => !IsBusy && (!string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0);

    /// <summary>取消按钮可用性。</summary>
    public bool CanCancel => IsBusy;

    /// <summary>
    /// 是否允许切换会话。
    /// 流式回复期间必须锁住：回复是往「当前会话」的消息集合里追加的，
    /// 中途切走会把内容写进别的会话（用户反馈的「对话串了」）。
    /// </summary>
    public bool CanSwitchConversation => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanSwitchConversation));
    }

    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(CanSend));

    partial void OnSelectedConversationChanged(AiConversation? value)
    {
        // 记住这次停留的会话，下次进页面直接恢复到它
        if (value is not null && _settings.LastConversationId != value.Id)
        {
            _settings.LastConversationId = value.Id;
            _store.SaveSettings(_settings);
        }

        Messages.Clear();
        _history = new List<AiMessage>();

        if (value is null)
        {
            return;
        }

        foreach (var message in value.Messages)
        {
            // 系统内部纠错提示不是用户说的话，恢复时丢掉（否则模型会一直以为用户说过）
            if (message.Role == "user" && AiInternalMarkup.IsSystemCorrection(message.Content))
            {
                continue;
            }

            // 过滤流式中断留下的空占位（空 assistant 消息会让下一轮请求被服务端拒绝）
            var isEmptyAssistant = message.Role == "assistant"
                                   && string.IsNullOrWhiteSpace(message.Content)
                                   && !message.IsError
                                   && message.ToolCalls is not { Count: > 0 };

            if (isEmptyAssistant)
            {
                continue;
            }

            // 清洗历史：旧会话里可能存着网关泄漏的 <result> 内部推理标记
            // （有一次存了 28213 字、3098 个空标记）。这些垃圾如果原样回传给模型，
            // 会持续污染上下文、把模型带进同样的循环。恢复时就地清掉。
            if (message.Role == "assistant" && !string.IsNullOrEmpty(message.Content))
            {
                // 先清 DSML 泄漏（工具调用标记），再清内部推理标记
                var dsml = AiDsmlParser.Parse(message.Content);

                if (dsml.HasDsml)
                {
                    message.Content = dsml.CleanedText;
                }

                var markup = AiInternalMarkup.Strip(message.Content);

                if (markup.HasInternal)
                {
                    message.Content = markup.CleanedText;
                    message.Reasoning = string.IsNullOrWhiteSpace(message.Reasoning)
                        ? markup.InternalText
                        : message.Reasoning + "\n\n" + markup.InternalText;

                    // 洗完正文空了（整条都是内部推理）→ 这条消息没有价值，
                    // 更不能留一条空 assistant 消息在历史里（下一轮请求可能被服务端拒）
                    if (message.Content.Length == 0 && message.ToolCalls is not { Count: > 0 })
                    {
                        continue;
                    }
                }
            }

            _history.Add(message);

            // 工具结果属于「过程」：归并到最近一条 AI 消息里（默认折叠），
            // 不再单独占一行——之前从磁盘恢复旧会话时会把它们整条摊开显示。
            if (message.Role == "tool")
            {
                var owner = Messages.LastOrDefault(m => m.Role == "assistant");
                owner?.Steps.Add($"{message.ToolName ?? "工具"} → {Shorten(message.Content, 160)}");
                continue;
            }

            Messages.Add(ToBubble(message));
        }
    }

    partial void OnIsFullDiskChanged(bool value)
    {
        _settings.PermissionMode = value ? AiPermissionMode.FullDisk : AiPermissionMode.WorkspaceOnly;

        // 权限变了就把「本次会话都允许」作废，重新按新权限确认
        _writesApprovedForSession = false;
        _tools = new AiFileTools(_settings, ConfirmWriteAsync);
        _store.SaveSettings(_settings);
        UpdateStatusTexts();

        if (value)
        {
            _shell.ShowToast("权限已放开", "AI 将可在任意路径读写文件，且不再逐次确认。", ControlAppearance.Caution);
        }
        else
        {
            _shell.ShowToast("权限已收紧", "AI 只能在指定工作区内操作，写文件前会向你确认。", ControlAppearance.Success);
        }
    }

    [RelayCommand]
    private void NewConversation()
    {
        if (IsBusy)
        {
            return;
        }

        var now = DateTime.Now;
        var conversation = new AiConversation { Title = "新对话", UpdatedAt = now };

        // 每个会话一个独立目录（时间命名），附件与产出都放这里，删除会话时整目录删除
        conversation.StorageKey = $"{now:yyyyMMdd-HHmmss}_{conversation.Id[..4]}";
        Conversations.Insert(0, conversation);
        SelectedConversation = conversation;
        InputText = string.Empty;
        Persist();
    }

    [RelayCommand]
    private void DeleteConversation(AiConversation? conversation)
    {
        if (IsBusy || conversation is null)
        {
            return;
        }

        var folder = ConversationFolder(conversation);
        Conversations.Remove(conversation);
        TryDeleteDirectory(folder);

        if (ReferenceEquals(SelectedConversation, conversation))
        {
            SelectedConversation = Conversations.FirstOrDefault();
            if (SelectedConversation is null)
            {
                NewConversation();
            }
        }

        Persist();
    }

    /// <summary>进入重命名状态（就地编辑标题）。</summary>
    [RelayCommand]
    private void RenameConversation(AiConversation? conversation)
    {
        if (conversation is null)
        {
            return;
        }

        conversation.RenameDraft = conversation.Title;
        conversation.IsEditing = true;
    }

    /// <summary>提交重命名。</summary>
    [RelayCommand]
    private void CommitRename(AiConversation? conversation)
    {
        if (conversation is null)
        {
            return;
        }

        var title = (conversation.RenameDraft ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            conversation.Title = title;
        }

        conversation.IsEditing = false;
        Persist();
    }

    /// <summary>放弃重命名。</summary>
    [RelayCommand]
    private void CancelRename(AiConversation? conversation)
    {
        if (conversation is not null)
        {
            conversation.IsEditing = false;
        }
    }

    [RelayCommand]
    private void ChooseWorkspace()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 AI 工作区（AI 只能在此目录内读写）",
        };

        if (Directory.Exists(WorkspacePath))
        {
            dialog.InitialDirectory = WorkspacePath;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        WorkspacePath = dialog.FolderName;
        _settings.WorkspaceDirectory = dialog.FolderName;
        _tools = new AiFileTools(_settings, ConfirmWriteAsync);
        _store.SaveSettings(_settings);
        UpdateStatusTexts();
        _shell.ShowToast("工作区已设置", dialog.FolderName, ControlAppearance.Success);
    }

    [RelayCommand]
    private void OpenWorkspace()
    {
        try
        {
            if (!Directory.Exists(WorkspacePath))
            {
                Directory.CreateDirectory(WorkspacePath);
            }

            Process.Start(new ProcessStartInfo(WorkspacePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.ShowToast("提示", ex.Message, ControlAppearance.Caution);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "正在取消…";
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var text = InputText.Trim();
        if (text.Length == 0 && Attachments.Count == 0)
        {
            return;
        }

        if (SelectedConversation is null)
        {
            NewConversation();
        }

        var pending = Attachments.ToList();
        var attachmentBlock = BuildAttachmentBlock(pending, ConversationRelativePath());

        InputText = string.Empty;
        Attachments.Clear();
        OnPropertyChanged(nameof(CanSend));

        IsBusy = true;
        StatusText = "正在请求模型…";

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 界面只显示用户输入 + 附件名；回传给模型的内容里才带附件正文
        var displayText = text;
        if (pending.Count > 0)
        {
            var names = "📎 " + string.Join("、", pending.Select(a => a.Name));
            displayText = displayText.Length > 0 ? displayText + "\n\n" + names : names;
        }

        var modelContent = string.IsNullOrWhiteSpace(attachmentBlock)
            ? text
            : (text.Length > 0 ? text + "\n\n" : string.Empty) + attachmentBlock;

        var userMessage = new AiMessage
        {
            Role = "user",
            Content = modelContent,
            DisplayContent = displayText,
        };
        _history.Add(userMessage);
        Messages.Add(new ChatItem { Role = "user", Content = displayText });

        if (SelectedConversation is not null && SelectedConversation.Title == "新对话")
        {
            var seed = text.Length > 0 ? text : (pending.FirstOrDefault()?.Name ?? "新对话");
            SelectedConversation.Title = seed.Length > 24 ? seed[..24] + "…" : seed;
        }

        Persist();

        try
        {
            var anomalyRetries = 0;
            var finishedNormally = false;
            var continuations = 0;
            var round = 0;

            while (round < MaxToolRounds)
            {
                round++;
                token.ThrowIfCancellationRequested();

                var bubble = new ChatItem { Role = "assistant", IsStreaming = true };
                Messages.Add(bubble);

                // 助手消息在「收到第一个流式增量」时才放进历史：
                // ① 这样请求体里不会带一条「尚未生成」的空 assistant 消息；
                // ② 一旦开始输出就立刻入历史，之后边流边写，进程被关/断流也不会丢。
                var assistantMessage = new AiMessage { Role = "assistant" };
                var assistantAdded = false;

                void EnsureAssistantInHistory()
                {
                    if (assistantAdded)
                    {
                        return;
                    }

                    assistantAdded = true;
                    _history.Add(assistantMessage);
                }

                var lastSave = DateTime.UtcNow;

                var progress = new Progress<AiStreamUpdate>(update =>
                {
                    if (!string.IsNullOrEmpty(update.Status))
                    {
                        StatusText = update.Status;
                    }

                    if (!string.IsNullOrEmpty(update.ContentDelta))
                    {
                        EnsureAssistantInHistory();
                        bubble.Content += update.ContentDelta;
                        assistantMessage.Content = bubble.Content;
                    }

                    if (!string.IsNullOrEmpty(update.ReasoningDelta))
                    {
                        EnsureAssistantInHistory();
                        bubble.Reasoning += update.ReasoningDelta;
                        assistantMessage.Reasoning = bubble.Reasoning;
                    }

                    // 流式期间每 1.5 秒落盘一次（不逐字写盘）
                    if ((DateTime.UtcNow - lastSave).TotalMilliseconds >= 1500)
                    {
                        lastSave = DateTime.UtcNow;
                        Persist();
                    }
                });

                var result = await _chat
                    .CompleteAsync(_history, AiFileTools.Specs, progress, token)
                    .ConfigureAwait(true);

                bubble.IsStreaming = false;

                if (!result.Ok)
                {
                    bubble.Role = "error";
                    bubble.Content = result.Error ?? "请求失败。";
                    assistantMessage.IsError = true;
                    assistantMessage.Content = bubble.Content;
                    StatusText = "请求失败";
                    Persist();
                    _shell.ShowToast("AI 请求失败", result.Error ?? "未知错误", ControlAppearance.Danger);
                    finishedNormally = true;
                    break;
                }

                // 用消毒后的完整文本覆盖（流式拼接可能残留孤立代理项）
                bubble.Content = AiTextSanitizer.Clean(result.Content);
                bubble.Reasoning = AiTextSanitizer.Clean(result.Reasoning);

                assistantMessage.Content = bubble.Content;
                assistantMessage.Reasoning = bubble.Reasoning;
                assistantMessage.ToolCalls = result.ToolCalls.Count > 0 ? result.ToolCalls.ToList() : null;

                // 有工具调用、或有正文/思考 → 入历史；什么都没有 → 不入（避免空消息）
                if (!assistantAdded
                    && (assistantMessage.Content.Length > 0
                        || assistantMessage.Reasoning.Length > 0
                        || assistantMessage.ToolCalls is { Count: > 0 }))
                {
                    EnsureAssistantInHistory();
                }

                Persist();

                UpdateStatusTexts();

                if (result.ToolCalls.Count == 0)
                {
                    // 异常轮：输出被 max_tokens 截断，或网关把 DSML 工具调用标记漏进了正文
                    // （模型本该调工具，却没拿到结构化 tool_calls）。
                    // 这两种都不是「模型说完了」——以前在这里直接 break，
                    // 于是用户看到的就是「活没干完，任务却报完成」。
                    var truncated = result.FinishReason == "length";

                    // 异常轮：截断 / DSML 泄漏 / 只有内部推理没有正文 / 空转循环吐标记
                    var abnormal = truncated
                                   || result.DsmlLeak
                                   || result.ReasoningOnly
                                   || result.MarkerLoop;

                    if (!abnormal)
                    {
                        StatusText = "完成";
                        finishedNormally = true;
                        break;
                    }

                    if (anomalyRetries >= MaxAnomalyRetries)
                    {
                        StatusText = "输出异常，已停止";
                        Messages.Add(new ChatItem
                        {
                            Role = "error",
                            Content = result.MarkerLoop
                                ? "模型连续多次陷入重复输出内部标记的循环，已停止。请重试，或把任务拆小一点。"
                                : result.ReasoningOnly
                                    ? "模型连续多次只输出了内部思考、没有给出可读回答，已停止。请重试或换个说法。"
                                    : result.DsmlLeak
                                        ? "模型连续多次输出了无法解析的工具调用标记，已停止。请重试，或换个说法让它用现有工具完成。"
                                        : "模型输出连续多次被长度限制截断，已停止。可以把任务拆小一点再试。",
                        });
                        Persist();
                        finishedNormally = true;
                        break;
                    }

                    anomalyRetries++;

                    // 这一轮正文是空的（只有内部推理）→ 把这条空 assistant 消息从历史里摘掉，
                    // 否则下一轮请求会带着一条空消息出去
                    if (assistantMessage.Content.Length == 0)
                    {
                        _history.Remove(assistantMessage);
                    }

                    StatusText = result.MarkerLoop
                        ? "模型陷入重复输出，正在重试…"
                        : result.ReasoningOnly
                            ? "只输出了思考内容，正在重试…"
                            : result.DsmlLeak
                                ? "工具调用格式异常，正在重试…"
                                : "输出被截断，正在续写…";

                    // 追加一条纠正提示，让模型换种方式再来一轮
                    _history.Add(new AiMessage
                    {
                        Role = "user",
                        Content = AiInternalMarkup.SystemCorrectionMarker + BuildCorrectionPrompt(result),
                    });

                    Messages.Add(new ChatItem { Role = "tool", Content = $"（{StatusText}）" });
                    Persist();

                    await Task.Delay(600, token).ConfigureAwait(true);
                    continue;
                }

                // 正常拿到工具调用，说明这一轮是健康的，重置异常计数
                anomalyRetries = 0;

                foreach (var call in result.ToolCalls)
                {
                    token.ThrowIfCancellationRequested();

                    StatusText = $"正在执行 {call.Name}…";

                    var output = await _tools.ExecuteAsync(call.Name, call.Arguments, token).ConfigureAwait(true);

                    var toolMessage = new AiMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        Content = output,
                    };
                    _history.Add(toolMessage);

                    // 工具调用不单独占正文，折叠进「过程」里
                    bubble.Steps.Add($"{call.Name}（{Shorten(call.Arguments, 80)}）→ {Shorten(output, 160)}");
                    Persist();
                }

                // 轮次用尽，但上一轮还在调工具 → 说明任务没干完，自动续跑，
                // 而不是硬生生结束让用户以为「任务完成」。
                if (round >= MaxToolRounds && continuations < MaxAutoContinuations)
                {
                    continuations++;
                    round = 0;

                    _history.Add(new AiMessage
                    {
                        Role = "user",
                        Content = "请继续完成剩下的工作，直到全部做完再汇报。",
                    });

                    Messages.Add(new ChatItem { Role = "tool", Content = "（工具轮次用尽，正在自动续跑…）" });
                    StatusText = "正在自动续跑…";
                    Persist();

                    await Task.Delay(600, token).ConfigureAwait(true);
                    continue;
                }

                StatusText = "模型正在继续…";

                // 轮次之间稍作停顿，避免连续请求把通道打到限流
                await Task.Delay(1200, token).ConfigureAwait(true);
            }

            if (!finishedNormally)
            {
                StatusText = "达到工具轮次上限";
                Messages.Add(new ChatItem
                {
                    Role = "tool",
                    Content = "（已达到本轮工具调用次数上限，任务可能还没做完。你可以说「继续」让我接着做。）",
                });
            }

            Persist();
            UpdateStatusTexts();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            Messages.Add(new ChatItem { Role = "tool", Content = "已取消本次请求。" });
            Persist();
        }
        catch (Exception ex)
        {
            StatusText = "出错";
            Messages.Add(new ChatItem { Role = "error", Content = ex.Message });
            Persist();
            _shell.ShowToast("AI 办公出错", ex.Message, ControlAppearance.Danger);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;

            if (_store.LastSaveError is { Length: > 0 } saveError)
            {
                _shell.ShowToast("会话保存失败", saveError, ControlAppearance.Caution);
            }
        }
    }

    /// <summary>接收拖入的文件：复制进工作区 <c>_attachments/</c>，文本类顺带内联内容。</summary>
    public void AddAttachments(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        string root;
        try
        {
            root = EnsureWorkspace();
        }
        catch (Exception ex)
        {
            _shell.ShowToast("附件添加失败", $"无法创建工作区：{ex.Message}", ControlAppearance.Danger);
            return;
        }

        // 附件放进「当前会话」自己的目录，删会话时一并清掉
        var attachDir = ConversationFolder();
        var added = 0;

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    continue;
                }

                var info = new FileInfo(path);
                if (!info.Exists
                    || Attachments.Any(a => string.Equals(a.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Directory.CreateDirectory(attachDir);

                var target = Path.Combine(attachDir, info.Name);
                var index = 1;
                while (File.Exists(target)
                       && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    target = Path.Combine(
                        attachDir,
                        $"{Path.GetFileNameWithoutExtension(info.Name)} ({index++}){info.Extension}");
                }

                if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(path, target, overwrite: true);
                }

                // 统一走 AiDocumentReader：文本/表格/文档都能取到干净文本，
                // 图片与二进制明确告知，绝不把字节流塞给模型
                var outcome = AiDocumentReader.Read(target);
                var isText = outcome.Ok;
                var preview = outcome.Ok ? outcome.Text : null;

                var kindText = outcome.Kind switch
                {
                    AiDocumentReader.Kind.Spreadsheet => "表格",
                    AiDocumentReader.Kind.Document => "文档",
                    AiDocumentReader.Kind.Text => "文本",
                    AiDocumentReader.Kind.Image => "图片（不可读）",
                    AiDocumentReader.Kind.Missing => "丢失",
                    _ => "二进制（不可读）",
                };

                Attachments.Add(new AiAttachment
                {
                    SourcePath = path,
                    WorkspaceFullPath = target,
                    WorkspaceRelativePath = Path.GetRelativePath(root, target).Replace('\\', '/'),
                    Name = info.Name,
                    Size = new FileInfo(target).Length,
                    TextPreview = preview,
                    IsText = isText,
                    KindText = kindText,
                });

                added++;
            }
            catch (Exception ex)
            {
                _shell.ShowToast("附件添加失败", $"{Path.GetFileName(path)}：{ex.Message}", ControlAppearance.Caution);
            }
        }

        if (added > 0)
        {
            OnPropertyChanged(nameof(CanSend));
            StatusText = $"已附加 {Attachments.Count} 个文件（已复制到工作区 _attachments/）";
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AiAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        Attachments.Remove(attachment);
        OnPropertyChanged(nameof(CanSend));
    }

    private string EnsureWorkspace()
    {
        var root = string.IsNullOrWhiteSpace(_settings.WorkspaceDirectory)
            ? AiSettings.DefaultWorkspace
            : _settings.WorkspaceDirectory;

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    /// <summary>某个会话的专属目录（工作区下 _conversations/&lt;时间名&gt;）。</summary>
    private string ConversationFolder(AiConversation? conversation = null)
    {
        var root = EnsureWorkspace();
        var target = conversation ?? SelectedConversation;
        var key = string.IsNullOrWhiteSpace(target?.StorageKey) ? "default" : target!.StorageKey;
        return Path.Combine(root, "_conversations", key);
    }

    /// <summary>会话目录相对工作区的路径（给模型看的）。</summary>
    private string ConversationRelativePath()
    {
        try
        {
            var root = EnsureWorkspace();
            return Path.GetRelativePath(root, ConversationFolder()).Replace('\\', '/');
        }
        catch
        {
            return "_conversations";
        }
    }

    /// <summary>删除目录（尽力而为）。</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 目录被占用时忽略，不影响会话删除
        }
    }

    /// <summary>把附件拼进回传给模型的内容（含工作区相对路径，便于模型用 read_file 深读）。</summary>
    private static string BuildAttachmentBlock(IReadOnlyList<AiAttachment> items, string conversationRelative)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"本会话目录：{conversationRelative}（产出文件建议放这里）");
        builder.AppendLine("【附件】用户拖入的文件已复制到工作区，可用 read_file 读取（下面是工作区相对路径）：");

        foreach (var item in items)
        {
            builder.AppendLine($"- {item.Name}（{item.SizeText}）：{item.WorkspaceRelativePath}");

            if (item.TextPreview is not null)
            {
                builder.AppendLine("  内容：");
                builder.AppendLine("  ```");
                builder.AppendLine(item.TextPreview);
                builder.AppendLine("  ```");
            }
            else
            {
                builder.AppendLine("  （二进制或非文本文件，只能看到文件名与大小；如需内容请提供文本版本）");
            }
        }

        return builder.ToString();
    }

    /// <summary>写文件前的确认（仅工作区模式触发）。</summary>
    /// <summary>根据异常类型生成纠错提示，让模型换种方式再来一轮。</summary>
    private static string BuildCorrectionPrompt(AiTurnResult result)
    {
        if (result.MarkerLoop)
        {
            return "上一轮你陷入了重复输出内部标记的循环。请停止重复，直接给出面向用户的回答或调用工具，"
                   + "不要输出任何形如 <result> 的内部标记。";
        }

        if (result.ReasoningOnly)
        {
            return "上一轮你只输出了内部思考内容，没有给用户可读的回答。"
                   + "请直接给出面向用户的回答（思考过程不需要写出来），不要输出任何形如 <result> 的内部标记。";
        }

        if (result.DsmlLeak)
        {
            return "上一轮你输出的工具调用标记无法被解析。请只通过平台提供的结构化工具调用接口发起调用，"
                   + "不要在正文里输出任何形如 ｜DSML｜ 的标记。";
        }

        return "上一轮你的输出被长度限制截断了。请接着上一轮继续，把没做完的部分做完。";
    }

    /// <summary>
    /// 写文件前的确认。
    /// <para>
    /// 以前用 <c>System.Windows.MessageBox.Show</c>：WPF 的 MessageBox 无法指定 owner，
    /// 在 Windows 11 + Mica 窗口下经常弹到主窗口<b>后面</b>——用户看不见它，但它在等点击，
    /// 界面就像卡死；等点到了若选了「否」，模型又会停下，看起来像「任务断了」。
    /// </para>
    /// 现在用带 owner 且 Topmost 的自定义窗口，一定能被看到；并提供「本次会话都允许」
    /// 避免连续写多个文件时反复打断。
    /// </summary>
    private Task<bool> ConfirmWriteAsync(AiWriteRequest request)
    {
        if (_writesApprovedForSession)
        {
            return Task.FromResult(true);
        }

        var action = request.IsNewFile
            ? $"新建文件（{request.Content.Length:N0} 字符）"
            : $"覆盖已有文件（原 {request.ExistingLength:N0} 字节 → 新 {request.Content.Length:N0} 字符）";

        var preview = Shorten(request.Content, 1200);

        // 先告诉用户程序在等他，别让人以为卡死
        StatusText = $"等待你确认写入：{Path.GetFileName(request.FullPath)}";

        try
        {
            var window = new AiWriteConfirmWindow(request.FullPath, action, preview)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };

            window.ShowDialog();

            if (window.Allowed && window.AllowForRestOfSession)
            {
                _writesApprovedForSession = true;
            }

            return Task.FromResult(window.Allowed);
        }
        catch
        {
            // 没有主窗口（例如自动化场景）时退回到 MessageBox
            var message =
                $"AI 请求写入本地文件：\n\n{request.FullPath}\n\n操作：{action}\n\n" +
                $"内容预览：\n{preview}\n\n是否允许？";

            var answer = System.Windows.MessageBox.Show(
                message,
                "AI 写入确认",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.No);

            return Task.FromResult(answer == System.Windows.MessageBoxResult.Yes);
        }
    }

    private void UpdateStatusTexts()
    {
        // 顶部只留一行用量（网关没有余额接口，所有 billing 路径都是 404）
        UsageText = "用量：" + _chat.SessionUsage.Format();
        PermissionText = IsFullDisk
            ? "权限：全盘（写文件不确认）"
            : "权限：仅工作区（写前确认）";
    }

    /// <summary>把当前会话状态落盘（每步都存，保证重启/切页不丢）。</summary>
    private void Persist()
    {
        List<AiConversation> snapshot;

        lock (_persistLock)
        {
            if (SelectedConversation is not null)
            {
                SelectedConversation.Messages = new List<AiMessage>(_history);
                SelectedConversation.UpdatedAt = DateTime.Now;
            }

            // 深拷贝一份：接下来要在后台线程序列化，而 UI 线程可能在同时改这些对象，
            // 直接传引用会有竞态（之前同步写盘也会让每次输出都卡顿一下）
            snapshot = Conversations.Select(CloneForSave).ToList();
        }

        // 文件 IO 放到后台，别拖慢流式输出
        _ = Task.Run(() =>
        {
            try
            {
                _store.SaveConversations(snapshot);
            }
            catch
            {
                // 保存失败不打断对话
            }
        });
    }

    private static AiConversation CloneForSave(AiConversation source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        UpdatedAt = source.UpdatedAt,
        StorageKey = source.StorageKey,
        Messages = source.Messages.Select(m => new AiMessage
        {
            Role = m.Role,
            Content = m.Content,
            DisplayContent = m.DisplayContent,
            Reasoning = m.Reasoning,
            ToolCallId = m.ToolCallId,
            ToolName = m.ToolName,
            ToolCalls = m.ToolCalls?.Select(c => new AiToolCall
            {
                Id = c.Id,
                Name = c.Name,
                Arguments = c.Arguments,
            }).ToList(),
            IsError = m.IsError,
            Time = m.Time,
        }).ToList(),
    };

    private static ChatItem ToBubble(AiMessage message) => new()
    {
        Role = message.IsError ? "error" : message.Role,
        Content = string.IsNullOrEmpty(message.DisplayContent) ? message.Content : message.DisplayContent!,
        Reasoning = message.Reasoning ?? string.Empty,
        TimeText = message.Time.ToString("HH:mm"),
    };

    private static string Shorten(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= max ? value : value[..max] + "…";
}
