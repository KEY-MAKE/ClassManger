using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using ClassManager.App.Services;

namespace ClassManager.App.Views;

/// <summary>
/// AI Key 配置窗口。
/// <para>
/// 用户可以用自己的 key 覆盖程序内置的（内置额度是所有使用者共享的，人一多就紧张）。
/// 保存到 <c>%AppData%\ClassManager\ai-credentials.json</c>，只在本机，不进仓库、不上传。
/// </para>
/// </summary>
public partial class AiKeySettingsWindow : Window
{
    public AiKeySettingsWindow()
    {
        InitializeComponent();
        RefreshSourceText();
        LoadExistingKeys();
    }

    private void RefreshSourceText()
    {
        var source = AiCredentialVault.Source;

        SourceText.Text = source switch
        {
            "user" => "当前：正在使用你自己填写的 Key",
            "builtin" => "当前：使用程序内置的共享额度",
            _ => "当前：不可用（没找到任何 Key）",
        };
    }

    /// <summary>如果已经填过，把现有的 key 回显出来方便修改。</summary>
    private void LoadExistingKeys()
    {
        try
        {
            if (!System.IO.File.Exists(AiCredentialVault.UserCredentialPath))
            {
                return;
            }

            var json = AiCredentialVault.ReadJson();

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("keys", out var keys)
                || keys.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var sb = new StringBuilder();

            foreach (var item in keys.EnumerateArray())
            {
                var value = item.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    sb.AppendLine(value);
                }
            }

            KeyBox.Text = sb.ToString().TrimEnd();
        }
        catch
        {
            // 回显失败无所谓
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var raw = KeyBox.Text?.Trim() ?? string.Empty;

        if (raw.Length == 0)
        {
            HintText.Text = "请先粘贴至少一个 Key。";
            return;
        }

        try
        {
            string json;

            if (raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                // 直接粘的完整 JSON —— 校验一下再存
                using var _ = JsonDocument.Parse(raw);
                json = raw;
            }
            else
            {
                // 每行一个 key
                var keys = new List<string>();

                foreach (var line in raw.Split('\n'))
                {
                    var key = line.Trim().Trim('\'', '"');

                    if (key.Length > 0)
                    {
                        keys.Add(key);
                    }
                }

                if (keys.Count == 0)
                {
                    HintText.Text = "没解析出有效的 Key。";
                    return;
                }

                // 沿用内置配置里的 baseUrl / model（用户只需要提供 key）
                var template = AiCredentialVault.Parse(AiCredentialVault.ReadJson());

                json = JsonSerializer.Serialize(new
                {
                    baseUrl = string.IsNullOrWhiteSpace(template?.BaseUrl)
                        ? "https://discovery-api.intern-ai.org.cn/v1"
                        : template!.BaseUrl,
                    model = string.IsNullOrWhiteSpace(template?.Model)
                        ? "deepseek-v4.1-flash-1017"
                        : template!.Model,
                    fallbacks = template?.Fallbacks ?? new List<string>(),
                    models = template?.Models ?? new List<AiModelOption>(),
                    keys,
                }, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            }

            AiCredentialVault.SaveUserJson(json);

            HintText.Text = "已保存。正在重新加载 AI 服务…";

            // 让正在运行的服务重新读凭据
            try
            {
                AppServices.ReloadAiCredentials();
            }
            catch
            {
                HintText.Text = "已保存，但重新加载失败 —— 重启程序后生效。";
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            HintText.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AiCredentialVault.ClearUserJson();
            AppServices.ReloadAiCredentials();
            KeyBox.Text = string.Empty;
            RefreshSourceText();
            HintText.Text = "已清除，回到程序内置的共享额度。";
        }
        catch (Exception ex)
        {
            HintText.Text = "清除失败：" + ex.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
