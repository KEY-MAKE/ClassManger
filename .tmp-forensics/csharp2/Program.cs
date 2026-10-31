// 临时探针：验证 AiChatService SSE 解析里的两个健壮性边界。
using System.Text.Json;
using System.Text.Json.Nodes;

Console.WriteLine("=== A. delta.content 为 JSON null 时 GetValue<string>() 会怎样？ ===");
var n1 = JsonNode.Parse("{\"delta\":{\"role\":\"assistant\",\"content\":null}}");
var c1 = n1["delta"]["content"];
Console.WriteLine($"  delta[\"content\"] 的 C# 类型 = {c1?.GetType().Name ?? "null(C#)"}");
Console.WriteLine($"  是否为 C# null : {c1 is null}");
try
{
    var s = c1?.GetValue<string>();
    Console.WriteLine($"  GetValue<string>() = {(s is null ? "null" : s)} （无异常）");
}
catch (Exception ex)
{
    Console.WriteLine($"  !! GetValue<string>() 抛出 {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("     → AiChatService.cs:483 会被这个异常打断，整轮请求失败。");
}

Console.WriteLine();
Console.WriteLine("=== B. delta.content 为 JSON 字符串 \"\" 时 ===");
var n2 = JsonNode.Parse("{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}");
var c2 = n2["delta"]["content"];
Console.WriteLine($"  GetValue<string>() = \"{c2?.GetValue<string>()}\" （无异常）");

Console.WriteLine();
Console.WriteLine("=== C. function.arguments 为 JSON 对象时 GetValue<string>() 会怎样？ ===");
var n3 = JsonNode.Parse("{\"function\":{\"arguments\":{\"path\":\"x\"}}}");
var a3 = n3["function"]["arguments"];
try
{
    var s = a3?.GetValue<string>();
    Console.WriteLine($"  GetValue<string>() = {s} （无异常）");
}
catch (Exception ex)
{
    Console.WriteLine($"  !! 抛出 {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("     → 若网关把 arguments 发成对象，AiChatService.cs:523 会整轮崩溃。");
}

Console.WriteLine();
Console.WriteLine("=== D. finish_reason 字段在 SSE 里长什么样（我们目前完全没读） ===");
var n4 = JsonNode.Parse("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}");
Console.WriteLine($"  choices[0][\"finish_reason\"] = {n4["choices"][0]["finish_reason"]}");
Console.WriteLine($"  AiChatService.cs:477-482 只取 delta，从未读取 finish_reason。");
