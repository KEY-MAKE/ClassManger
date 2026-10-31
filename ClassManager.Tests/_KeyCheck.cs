using System;
using System.IO;
using System.Reflection;
using System.Text;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

public class _KeyCheck
{
    [Fact]
    public void Keys()
    {
        var log = Path.Combine(Path.GetTempPath(), "cm_keys.txt");
        var sb = new StringBuilder();

        // 直接用程序里那份加载逻辑（嵌入资源），验证"编译进 exe 的"到底有几个 key
        var m = typeof(AiChatService).GetMethod("LoadCredentials",
            BindingFlags.NonPublic | BindingFlags.Static);
        var cred = m!.Invoke(null, null)!;
        var t = cred.GetType();

        var keys = t.GetProperty("Keys")!.GetValue(cred) as System.Collections.IEnumerable;
        var n = 0;
        foreach (var k in keys!) { n++; sb.AppendLine($"  api{n}: {k!.ToString()![..12]}...{k.ToString()![^6..]}"); }

        sb.Insert(0, $"编译进程序的 key 数量 = {n}\n");
        sb.AppendLine($"  model   = {t.GetProperty("Model")!.GetValue(cred)}");
        sb.AppendLine($"  baseUrl = {t.GetProperty("BaseUrl")!.GetValue(cred)}");

        File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false));
    }
}
