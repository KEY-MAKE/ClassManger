using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClassManager.App.Models;
using ClassManager.App.Services;
using ClassManager.App.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 「异常轮」判定与重试机制的端到端行为（ViewModel 层）。
/// <para>
/// 依据：docs/QA-v0.27.md。要回答四个问题：
/// ① 空转循环（MarkerLoop）是否真的会被打断，还是死循环；
/// ② 正常轮是否会被误判成异常轮（尤其是正文很短的正常回答）；
/// ③ 历史清洗会不会改坏用户/tool 的合法内容、是否写回磁盘；
/// ④ 纠错重试带回去的历史本身是否合规。
/// </para>
/// <para>
/// 关键：<see cref="FakeChat"/> 复刻了 <c>AiChatService.TryOnceAsync</c> 末端的真实管线
/// （<c>AiDsmlParser.Parse</c> → <c>AiInternalMarkup.Strip</c> → 三个标志位），
/// 否则测到的就不是线上行为。
/// </para>
/// </summary>
public sealed class AiAnomalyRoundTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspace;

    public AiAnomalyRoundTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cm-ai-anomaly-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_tempDir, "ws");
        Directory.CreateDirectory(_workspace);

        // 让 ViewModel 用临时目录，不污染用户的 %AppData%
        File.WriteAllText(
            Path.Combine(_tempDir, "ai-settings.json"),
            "{\"WorkspaceDirectory\":\"" + _workspace.Replace("\\", "\\\\") + "\"}");

        typeof(AppServices)
            .GetField("<AiStore>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new AiStore(_tempDir));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 忽略
        }
    }

    // -------------------------------------------------------------- 测试替身

    private sealed class FakeShell : IShell
    {
        public List<string> Toasts { get; } = new();

        public void SetStatus(string message)
        {
        }

        public void SetCurrentModule(string moduleName)
        {
        }

        public void ShowToast(string title, string message, ControlAppearance appearance) =>
            Toasts.Add(title + "|" + message);

        public Task ImportRosterAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// 复刻 AiChatService 的协议层管线：把「服务端返回的原始正文」按线上同样的方式
    /// 加工成 <see cref="AiTurnResult"/>。
    /// </summary>
    private sealed class FakeChat : IAiChatService
    {
        private readonly Queue<AiTurnResult> _turns = new();

        public int Calls { get; private set; }

        /// <summary>每次请求带出去的完整历史（用来检查回传污染）。</summary>
        public List<List<AiMessage>> SentHistories { get; } = new();

        public string ModelName => "fake-model";

        public string ActiveKeyLabel => "api1";

        public AiUsageTotals SessionUsage { get; } = new();

        /// <summary>直接指定一轮的结果（用于精确构造异常场景）。</summary>
        public FakeChat Enqueue(AiTurnResult turn)
        {
            _turns.Enqueue(turn);
            return this;
        }

        /// <summary>
        /// 按真实管线加工一段原始正文后返回，模拟「服务端真的这么回的」。
        /// </summary>
        public FakeChat EnqueueRaw(string rawContent, string rawReasoning = "", string finishReason = "stop")
        {
            var raw = AiTextSanitizer.Clean(rawContent);
            var dsml = AiDsmlParser.Parse(raw);
            var withoutDsml = dsml.HasDsml ? dsml.CleanedText : raw;
            var markup = AiInternalMarkup.Strip(withoutDsml);

            var reasoning = AiTextSanitizer.Clean(rawReasoning);
            if (!string.IsNullOrWhiteSpace(markup.InternalText))
            {
                reasoning = string.IsNullOrWhiteSpace(reasoning)
                    ? markup.InternalText
                    : reasoning + "\n\n" + markup.InternalText;
            }

            var calls = dsml.Calls.Select(c => new AiToolCall
            {
                Id = "dsml_" + Guid.NewGuid().ToString("N"),
                Name = c.Name,
                Arguments = c.ArgumentsJson,
            }).ToList();

            return Enqueue(new AiTurnResult
            {
                Ok = true,
                Content = markup.CleanedText,
                Reasoning = reasoning,
                ToolCalls = calls,
                FinishReason = finishReason,
                DsmlLeak = dsml.Unparsable,
                ReasoningOnly = markup.ReasoningOnly,
                MarkerLoop = markup.LooksLikeLoop,
            });
        }

        public Task<AiTurnResult> CompleteAsync(
            IReadOnlyList<AiMessage> history,
            IReadOnlyList<AiToolSpec> tools,
            IProgress<AiStreamUpdate>? stream,
            CancellationToken ct)
        {
            Calls++;
            SentHistories.Add(history.Select(m => new AiMessage
            {
                Role = m.Role,
                Content = m.Content,
                Reasoning = m.Reasoning,
                ToolName = m.ToolName,
            }).ToList());

            var turn = _turns.Count > 0
                ? _turns.Dequeue()
                : new AiTurnResult { Ok = true, Content = "（默认回答）" };

            return Task.FromResult(turn);
        }
    }

    private static AiOfficeViewModel CreateVm(FakeChat chat)
    {
        // ViewModel 构造时从 AppServices.Ai 取聊天服务，必须在构造前塞进去
        typeof(AppServices)
            .GetField("<Ai>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, chat);

        return new AiOfficeViewModel(new FakeShell());
    }

    private static List<AiMessage> History(AiOfficeViewModel vm)
    {
        var field = typeof(AiOfficeViewModel)
            .GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (List<AiMessage>)field.GetValue(vm)!;
    }

    private static async Task SendAsync(AiOfficeViewModel vm, string text = "你好")
    {
        vm.InputText = text;
        await vm.SendCommand.ExecuteAsync(null);
    }

    // -------------------------------------------------------------- ① 空转循环会被打断

    [Fact]
    public async Task Marker_loop_is_broken_after_bounded_retries()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        // 服务端每一轮都吐 3098 个 <result>（真实空转形态）
        for (var i = 0; i < 10; i++)
        {
            chat.EnqueueRaw(string.Concat(Enumerable.Repeat("<result>\n", 3098)));
        }

        await SendAsync(vm);

        // 1 轮原始 + 最多 2 次纠错重试 = 3 次请求，之后必须停（不是死循环）
        Assert.Equal(3, chat.Calls);

        // 必须给出明确的失败提示，而不是静默结束
        var errors = vm.Messages.Where(m => m.Role == "error").ToList();
        Assert.Single(errors);
        Assert.Contains("循环", errors[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_loop_retry_sends_correction_prompt_back_to_model()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        chat.EnqueueRaw(string.Concat(Enumerable.Repeat("<result>\n", 30)));
        chat.EnqueueRaw("这是正常回答。");

        await SendAsync(vm);

        Assert.Equal(2, chat.Calls);

        // 第二次请求的历史里必须带上纠错提示
        var second = chat.SentHistories[1];
        Assert.Contains(second, m => m.Role == "user" && m.Content.Contains("<result>"));
    }

    // -------------------------------------------------------------- ② 正常轮不被误伤

    [Fact]
    public async Task Short_normal_answer_is_not_treated_as_abnormal()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        chat.EnqueueRaw("好。");

        await SendAsync(vm);

        Assert.Equal(1, chat.Calls);
        Assert.DoesNotContain(vm.Messages, m => m.Role == "error");
    }

    [Fact]
    public async Task Normal_answer_with_angle_brackets_is_not_treated_as_abnormal()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        // 正常代码回答：只有 1 个 '<'，远低于循环阈值
        chat.EnqueueRaw("if (a < b) { }");

        await SendAsync(vm);

        Assert.Equal(1, chat.Calls);
        Assert.DoesNotContain(vm.Messages, m => m.Role == "error");
    }

    /// <summary>
    /// 缺陷 P0-A 的现状断言：<c>markerCount</c> 把正文里所有 '&lt;' 都算成内部标记，
    /// 于是 20 个以上 '&lt;' 的正常代码/HTML 回答会被误判成空转循环。
    /// <para>修复后应把本用例改成「1 次请求、无错误」。</para>
    /// </summary>    /// <summary>
    /// 修复后：含 20 个 '&lt;' 的合法代码回答不再被误判成空转循环。
    /// （缺陷：标记计数原本数的是 '&lt;' 的个数，导致正常代码被当成循环 → 白重试 3 次还报错。）
    /// </summary>

    [Fact]
    public async Task Current_behaviour_code_with_many_less_than_signs_is_misjudged_as_loop()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        var code = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"if (a{i} < b{i}) {{ }}"));

        for (var i = 0; i < 5; i++)
        {
            chat.EnqueueRaw(code);
        }

        await SendAsync(vm);

        // 修复后：合法的代码回答不该被判成空转循环 —— 一次就通过，也不会有错误气泡
        Assert.Equal(1, chat.Calls);
        Assert.DoesNotContain(vm.Messages, m => m.Role == "error");
    }

    // -------------------------------------------------------------- ③ 历史清洗

    [Fact]
    public void History_cleaning_keeps_user_and_tool_messages_intact()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        var conv = vm.SelectedConversation!;
        conv.Messages.Add(new AiMessage
        {
            Role = "user",
            Content = "用户自己输入的 <result> 字样必须原样保留",
        });
        conv.Messages.Add(new AiMessage
        {
            Role = "tool",
            ToolName = "read_file",
            Content = "文件内容里有 </result> 这种文本",
        });
        conv.Messages.Add(new AiMessage
        {
            Role = "assistant",
            Content = "这是结论。\n<result>\n这是内部思考。</result>",
        });

        // 切走再切回 → 触发 OnSelectedConversationChanged
        vm.SelectedConversation = null;
        vm.SelectedConversation = conv;

        var history = History(vm);

        Assert.Equal("用户自己输入的 <result> 字样必须原样保留", history[0].Content);
        Assert.Equal("文件内容里有 </result> 这种文本", history[1].Content);
        Assert.DoesNotContain("<result>", history[2].Content, StringComparison.Ordinal);
        Assert.Contains("这是结论。", history[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void History_cleaning_does_not_touch_disk_immediately()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        var conv = vm.SelectedConversation!;
        conv.Messages.Add(new AiMessage
        {
            Role = "assistant",
            Content = "结论\n<result>内部思考</result>",
        });

        vm.SelectedConversation = null;
        vm.SelectedConversation = conv;

        // 内存里已经洗掉了
        Assert.DoesNotContain("<result>", History(vm)[0].Content, StringComparison.Ordinal);

        // 但切换会话本身不落盘。
        // 注意：落盘是后台异步的（Persist 走 Task.Run），所以先等构造时那次落盘完成，
        // 记下内容，再断言切换之后内容没变 —— 而不是断言「文件不存在」（那样会因时序 flaky）。
        var path = Path.Combine(_tempDir, "ai-conversations.json");

        string? before = null;

        for (var i = 0; i < 60; i++)
        {
            if (File.Exists(path))
            {
                before = File.ReadAllText(path);
                break;
            }

            Thread.Sleep(50);
        }

        Assert.NotNull(before);

        vm.SelectedConversation = null;
        vm.SelectedConversation = conv;

        Thread.Sleep(300);

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task History_cleaning_is_persisted_on_next_save()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        var conv = vm.SelectedConversation!;
        conv.Messages.Add(new AiMessage
        {
            Role = "assistant",
            Content = "结论\n<result>内部思考</result>",
        });

        vm.SelectedConversation = null;
        vm.SelectedConversation = conv;

        // 触发一次落盘
        chat.EnqueueRaw("好的");
        await SendAsync(vm);

        // 落盘在后台线程，等它完成
        var path = Path.Combine(_tempDir, "ai-conversations.json");
        for (var i = 0; i < 100 && !File.Exists(path); i++)
        {
            await Task.Delay(50);
        }

        for (var i = 0; i < 20; i++)
        {
            var json = File.ReadAllText(path);
            if (!json.Contains("<result>", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.DoesNotContain("<result>", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// 缺陷 P1-B 的现状断言：历史清洗只处理了 &lt;result&gt; 系标记，
    /// 完全没走 DSML 解析 → 旧会话里泄漏的 ｜DSML｜ 垃圾仍然会回传给模型。
    /// </summary>    /// <summary>修复后：历史清洗会把旧消息里的 DSML 垃圾一并清掉。</summary>

    [Fact]
    public void Current_behaviour_history_cleaning_ignores_dsml_garbage()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        var conv = vm.SelectedConversation!;
        conv.Messages.Add(new AiMessage
        {
            Role = "assistant",
            Content = "结论\n｜DSML｜ parameter>\n</｜DSML｜ invoke>",
        });

        vm.SelectedConversation = null;
        vm.SelectedConversation = conv;

        // 修复后：DSML 垃圾也要清掉，不能留在正文里
        Assert.DoesNotContain("DSML", History(vm)[0].Content, StringComparison.Ordinal);
        Assert.Contains("结论", History(vm)[0].Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// 缺陷 P0-B 的现状断言：历史清洗把「只有内部标记」的旧 assistant 消息洗成空正文，
    /// 但空正文检查发生在清洗<b>之前</b> → 于是 _history 里多出一条空 assistant 消息。
    /// <para>
    /// 而 <c>AiOfficeViewModel.OnSelectedConversationChanged</c> 自己的注释明确写着
    /// 「空 assistant 消息会让下一轮请求被服务端拒绝」——清洗自己制造了它要防的东西。
    /// </para>
    /// </summary>    /// <summary>修复后：清洗不会造出「空 assistant 消息」这种下一轮可能被服务端拒的东西。</summary>

    [Fact]
    public void Current_behaviour_history_cleaning_creates_empty_assistant_message()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        var conv = vm.SelectedConversation!;
        conv.Messages.Add(new AiMessage
        {
            Role = "assistant",
            Content = string.Concat(Enumerable.Repeat("<result>\n", 3098)),
        });

        vm.SelectedConversation = null;
        vm.SelectedConversation = conv;

        var history = History(vm);

        // 修复后：整条都是内部推理的旧消息直接丢弃，不留空 assistant 消息
        Assert.DoesNotContain(history, m => m.Role == "assistant" && string.IsNullOrEmpty(m.Content));
    }

    // -------------------------------------------------------------- ④ 纠错重试带回去的历史

    /// <summary>
    /// 现状断言：纠错提示以 role=user 的消息写进 _history，
    /// 于是它会（a）被当成「用户说的话」显示出来，（b）永远留在会话文件里。
    /// </summary>    /// <summary>
    /// 修复后：纠错提示带内部标记，重新打开会话时会被过滤掉，
    /// 不会冒充「用户说的话」显示成一条「你」的气泡。
    /// </summary>

    [Fact]
    public async Task Current_behaviour_correction_prompt_is_stored_as_user_message()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        chat.EnqueueRaw(string.Concat(Enumerable.Repeat("<result>\n", 30)));
        chat.EnqueueRaw("好了。");

        await SendAsync(vm);

        var history = History(vm);

        // 修复后：纠错提示带内部标记，能被识别出来
        Assert.Contains(
            history,
            m => m.Role == "user" && AiInternalMarkup.IsSystemCorrection(m.Content));

        // 重新打开会话：它不是用户说的话，不该渲染成一条「你」的气泡
        vm.SelectedConversation = null;
        vm.SelectedConversation = vm.Conversations.First();

        Assert.DoesNotContain(
            vm.Messages,
            m => m.Role == "user" && AiInternalMarkup.IsSystemCorrection(m.Content));
    }

    /// <summary>
    /// 现状断言：异常轮之后 _history 里留下 content 为空的 assistant 消息。
    /// <para>
    /// 而 <c>AiOfficeViewModel.cs:237</c> 的注释明确写着「空 assistant 消息会让下一轮请求被
    /// 服务端拒绝」——历史恢复路径专门过滤了这种消息，但纠错重试路径自己又制造了一个。
    /// 两处对同一条件的判断互相矛盾。
    /// </para>
    /// <para>
    /// 实测（.tmp-forensics/empty_assistant_probe.py，3/3 HTTP 200）：本项目当前用的网关
    /// 其实<b>接受</b>这种形态，所以不会真的 400；这是「自相矛盾 + 留垃圾」的问题，
    /// 不是「重试必然失败」。
    /// </para>
    /// </summary>    /// <summary>修复后：异常轮之后，历史里不会残留空 assistant 消息。</summary>

    [Fact]
    public async Task Current_behaviour_empty_assistant_message_is_left_in_history()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        chat.EnqueueRaw(string.Concat(Enumerable.Repeat("<result>\n", 30)));
        chat.EnqueueRaw("好了。");

        await SendAsync(vm);

        var second = chat.SentHistories[1];

        // 修复后：异常轮留下的空 assistant 消息会被摘掉
        Assert.DoesNotContain(second, m => m.Role == "assistant" && string.IsNullOrEmpty(m.Content));
    }

    /// <summary>
    /// 现状断言：纠错提示以 role=user 落进会话文件 → 之后每一轮请求都会把它
    /// 当成「用户说的话」回传给模型，且界面上会显示成一条「你」的气泡。
    /// </summary>    /// <summary>
    /// 修复后：纠错提示落盘时带内部标记，重新打开会话时会被过滤 ——
    /// 不会每一轮都把「用户说过的话」回传给模型。
    /// </summary>

    [Fact]
    public async Task Current_behaviour_correction_prompt_is_persisted_to_disk()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        chat.EnqueueRaw(string.Concat(Enumerable.Repeat("<result>\n", 30)));
        chat.EnqueueRaw("好了。");

        await SendAsync(vm);

        var path = Path.Combine(_tempDir, "ai-conversations.json");
        for (var i = 0; i < 100 && !File.Exists(path); i++)
        {
            await Task.Delay(50);
        }

        for (var i = 0; i < 20; i++)
        {
            if (File.ReadAllText(path).Contains(AiInternalMarkup.SystemCorrectionMarker, StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(50);
        }

        var json = File.ReadAllText(path);

        // 修复后：落盘的纠错提示带内部标记（可被识别），恢复时会被过滤
        Assert.Contains(AiInternalMarkup.SystemCorrectionMarker, json, StringComparison.Ordinal);

        vm.SelectedConversation = null;
        vm.SelectedConversation = vm.Conversations.First();

        Assert.DoesNotContain(
            vm.Messages,
            m => m.Role == "user" && AiInternalMarkup.IsSystemCorrection(m.Content));
    }

    // ------------------------------------------------------------ DSML 侧

    [Fact]
    public async Task Dsml_leak_is_retried_instead_of_ending_the_task()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        // 真实样本：invoke 头被网关吞掉，还原不出工具名
        chat.EnqueueRaw("｜DSML｜ parameter name=\"code\" string=\"true\">\nprint(1)\n"
                        + "</｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>");
        chat.EnqueueRaw("已经改好了，文件在会话目录里。");

        await SendAsync(vm);

        // 第一轮被判异常 → 重试，而不是直接结束任务
        Assert.Equal(2, chat.Calls);
        Assert.DoesNotContain(vm.Messages, m => m.Role == "error");

        // 界面上不能出现 ｜DSML｜ 乱码
        Assert.DoesNotContain(vm.Messages, m => m.Content.Contains("DSML", StringComparison.Ordinal));
    }
    /// <summary>修复后：正常回答里提到 ｜DSML｜ 不会被当成泄漏截断，也不会触发重试。</summary>

    [Fact]
    public async Task Normal_answer_mentioning_dsml_is_currently_truncated()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        // 用户问「DSML 是什么」，模型照实解释 —— 提到 ｜DSML｜ 不该被判成泄漏
        chat.EnqueueRaw("DSML 是 DeepSeek 的标记语言，形如 ｜DSML｜invoke name=\"read_file\"> 这样调用。");

        await SendAsync(vm);

        // 修复后：正文完整保留，也不会被当成故障去重试
        var bubble = vm.Messages.First(m => m.Role == "assistant");
        Assert.Equal("DSML 是 DeepSeek 的标记语言，形如 ｜DSML｜invoke name=\"read_file\"> 这样调用。", bubble.Content);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Dsml_leak_that_cannot_be_parsed_stops_after_bounded_retries()
    {
        var chat = new FakeChat();
        var vm = CreateVm(chat);

        for (var i = 0; i < 5; i++)
        {
            chat.EnqueueRaw("｜DSML｜ parameter>\n</｜DSML｜ invoke>");
        }

        await SendAsync(vm);

        Assert.Equal(3, chat.Calls);

        var errors = vm.Messages.Where(m => m.Role == "error").ToList();
        Assert.Single(errors);
        Assert.Contains("工具调用标记", errors[0].Content, StringComparison.Ordinal);
    }
}
