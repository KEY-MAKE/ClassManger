using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassManager.App.Models;
using ClassManager.App.Services;
using Xunit;

namespace ClassManager.Tests;

/// <summary>
/// 「能力不足时自我扩展」的工具契约。
/// <para>
/// 联网安装这类动作依赖外网与 pip 源，不适合放进单测；这里只锁定契约：
/// 工具存在、解释器可发现、危险包名会被拒绝。
/// </para>
/// </summary>
public class AiSelfExtensionTests
{
    [Fact]
    public void Self_extension_tools_are_declared()
    {
        var names = AiFileTools.Specs.Select(s => s.Name).ToArray();

        Assert.Contains("search_tool", names);
        Assert.Contains("install_tool", names);
        Assert.Contains("run_python", names);
        Assert.Contains("run_node", names);
        Assert.Contains("web_fetch", names);

        // 每个工具都要有说明，否则模型不知道该怎么用
        foreach (var spec in AiFileTools.Specs)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{spec.Name} 缺少说明");
        }
    }

    [Fact]
    public async Task Package_names_with_injection_attempts_are_rejected()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"cm_tools_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);

        var tools = new AiFileTools(
            new AiSettings { WorkspaceDirectory = workspace },
            _ => Task.FromResult(true));

        var dangerous = new[]
        {
            "requests; rm -rf /",
            "evil && calc.exe",
            "pkg --index-url http://evil.example.com",
            "../../etc/passwd",
        };

        foreach (var name in dangerous)
        {
            var result = await tools.ExecuteAsync(
                "install_tool",
                $"{{\"package\":{System.Text.Json.JsonSerializer.Serialize(name)}}}",
                CancellationToken.None);

            Assert.Contains("不合法", result, StringComparison.Ordinal);
        }

        Directory.Delete(workspace, true);
    }

    [Fact]
    public void Python_and_node_are_discovered_when_present()
    {
        var python = ToolInstaller.FindPython();
        var node = ToolInstaller.FindNode();

        // 这台机器上应该有；没有也不该崩，只是能力缺失
        if (python is not null)
        {
            Assert.True(File.Exists(python), "找到的 Python 路径必须存在：" + python);
        }

        if (node is not null)
        {
            Assert.True(File.Exists(node), "找到的 Node 路径必须存在：" + node);
        }
    }

    [Fact]
    public void Tools_are_installed_into_workspace_only()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"cm_tools_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);

        var installer = new ToolInstaller(workspace);

        Assert.StartsWith(Path.GetFullPath(workspace), Path.GetFullPath(installer.PythonToolsDirectory));
        Assert.StartsWith(Path.GetFullPath(workspace), Path.GetFullPath(installer.NodeToolsDirectory));

        Directory.Delete(workspace, true);
    }
}
