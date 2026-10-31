// 临时探针：用真实的 System.Text.Json.Nodes 语义，验证 BuildRequestBody 产出的 JSON 形状。
// 等价于 AiChatService.BuildRequestBody（AiChatService.cs:251-329）里关键的几行。
using System.Text.Json;
using System.Text.Json.Nodes;

var history = new List<Msg>
{
    new Msg("system", "你是一个本地 AI 助手。", null, null),
    new Msg("user", "列一下工作区。", null, null),
    new Msg("assistant", "", null, new List<Tc>
    {
        new Tc("chatcmpl-tool-b2dc4cfa8f4f7e1c", "list_files", "{\"path\": \"\"}"),
    }),
    new Msg("tool", "文件很多。", "chatcmpl-tool-b2dc4cfa8f4f7e1c", null),
};

Console.WriteLine("=== A. 现有实现：assistant.content = \"\"，arguments 为 string ===");
var a = Build(history);
Console.WriteLine(a);
Console.WriteLine();

Console.WriteLine("=== B. 对照：assistant.content = null ===");
var h2 = new List<Msg>();
foreach (var m in history)
{
    bool hasCalls = m.Calls is not null;
    h2.Add(new Msg(m.Role, hasCalls ? null : m.Content, m.ToolCallId, m.Calls));
}
Console.WriteLine(Build(h2));
Console.WriteLine();

Console.WriteLine("=== C. arguments 的 JSON 类型判定 ===");
var root = JsonNode.Parse(a);
var fn = root["messages"][2]["tool_calls"][0]["function"];
Console.WriteLine($"  arguments 节点类型 = {fn["arguments"].GetType().Name}");
Console.WriteLine($"  arguments 序列化 = {fn["arguments"].ToJsonString()}");
Console.WriteLine("  -> 是 JSON **字符串**（带转义引号），不是对象。符合 OpenAI 规范。");
Console.WriteLine();

Console.WriteLine("=== D. assistant.content 的 JSON 类型判定 ===");
var c = root["messages"][2]["content"];
Console.WriteLine($"  content 节点类型 = {c.GetType().Name}");
Console.WriteLine($"  content 序列化 = {c.ToJsonString()}");
Console.WriteLine("  -> 空字符串 \"\"，不是 null；字段也没被省略。");
Console.WriteLine();

Console.WriteLine("=== E. 对照：若 arguments 写成对象（仅示意，非本项目行为）===");
Console.WriteLine(new JsonObject { ["arguments"] = JsonNode.Parse("{\"path\":\"\"}") }.ToJsonString());
Console.WriteLine();

Console.WriteLine("=== F. tools[].parameters 里 properties 为空的对象 ===");
var p = root["tools"][0]["function"]["parameters"];
Console.WriteLine($"  {p.ToJsonString()}");
Console.WriteLine("  -> schema 里没有任何属性名，模型只能从中文 description 猜参数名。");

static string Build(IEnumerable<Msg> history)
{
    var messages = new JsonArray();
    foreach (var message in history)
    {
        var node = new JsonObject { ["role"] = message.Role };
        if (message.Role == "tool")
        {
            node["tool_call_id"] = message.ToolCallId ?? string.Empty;
            node["content"] = message.Content;
        }
        else
        {
            node["content"] = message.Content;               // <-- AiChatService.cs:272
            if (message.Calls is { Count: > 0 })
            {
                var arr = new JsonArray();
                foreach (var call in message.Calls)
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.Args,         // <-- AiChatService.cs:286
                        },
                    });
                }
                node["tool_calls"] = arr;
            }
        }
        messages.Add(node);
    }

    var root = new JsonObject
    {
        ["model"] = "deepseek-v4.1-flash-1017",
        ["messages"] = messages,
        ["max_tokens"] = 4096,
        ["stream"] = true,
    };

    root["tools"] = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "run_python",
                ["description"] = "执行一段 Python 脚本。参数：code 脚本内容。",
                ["parameters"] = JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}"),
            },
        },
    };
    root["tool_choice"] = "auto";

    return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

sealed class Msg
{
    public string Role { get; init; }
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public List<Tc>? Calls { get; init; }

    public Msg(string role, string? content, string? toolCallId, List<Tc>? calls)
    {
        Role = role; Content = content; ToolCallId = toolCallId; Calls = calls;
    }
}

sealed class Tc
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Args { get; init; }

    public Tc(string id, string name, string args)
    {
        Id = id; Name = name; Args = args;
    }
}
