using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassManager.App.Services;

/// <summary>
/// AI 凭据仓库：负责把「加密的内置凭据」和「用户自填的凭据」读出来。
/// <para>
/// <b>为什么要加密</b>：之前 <c>credentials.json</c> 以明文形式进了公开仓库，
/// 被爬虫扫走后四天烧光 6 把 key。现在明文只留在本机（.gitignore 排除），
/// 进仓库的是 AES 密文，用 <c>strings</c> 之类的手段扫不到 <c>sk-</c>。
/// </para>
/// <para>
/// <b>优先级</b>：<c>%AppData%\ClassManager\ai-credentials.json</c>（用户自填，明文）
/// 高于内置密文 —— 老师想用自己的 key 时，覆盖文件即可，不用重新编译。
/// </para>
/// <para>
/// <b>诚实说明</b>：加密提高的是「提取成本」，不是绝对安全。真正的零风险只有
/// 「程序不内置任何 key」。所以设置页提供了自填入口。
/// </para>
/// </summary>
public static class AiCredentialVault
{
    /// <summary>内置密文的资源名后缀。</summary>
    private const string EncryptedResourceSuffix = "ai.credentials.enc";

    // !! 改动这里必须同步改 tools/encrypt-credentials.py 的 _PASSPHRASE_PARTS !!
    // 拆成几段是刻意的：单看某一处常量拼不出完整口令，静态扫描更难命中。
    private static readonly string[] PassphraseParts =
    {
        "cm", "-ai-", "vault", "-2026", "-K#9", "xQ7",
    };

    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("classmanager-ai-vault");
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("credentials");

    /// <summary>用户自填的凭据路径（明文，只在用户自己机器上）。</summary>
    public static string UserCredentialPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClassManager",
        "ai-credentials.json");

    /// <summary>
    /// 读取凭据 JSON 文本。
    /// <para>返回 null 表示两条路都拿不到（用户没填 + 内置密文损坏）。</para>
    /// </summary>
    public static string? ReadJson()
    {
        // ① 用户自填优先
        try
        {
            if (File.Exists(UserCredentialPath))
            {
                var text = File.ReadAllText(UserCredentialPath, Encoding.UTF8);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch
        {
            // 读不了就退到内置
        }

        // ② 内置密文
        try
        {
            var encrypted = ReadEmbedded();

            if (encrypted is null)
            {
                return null;
            }

            return Decrypt(encrypted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>凭据来源（界面提示用）：user / builtin / none。</summary>
    public static string Source
    {
        get
        {
            try
            {
                if (File.Exists(UserCredentialPath)
                    && new FileInfo(UserCredentialPath).Length > 2)
                {
                    return "user";
                }
            }
            catch
            {
                // 忽略
            }

            return ReadEmbedded() is null ? "none" : "builtin";
        }
    }

    /// <summary>把用户自填的凭据写到 %AppData%（设置页保存时调用）。</summary>
    public static void SaveUserJson(string json)
    {
        var dir = Path.GetDirectoryName(UserCredentialPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(UserCredentialPath, json, new UTF8Encoding(false));
    }

    /// <summary>删掉用户自填凭据（回退到内置）。</summary>
    public static void ClearUserJson()
    {
        try
        {
            if (File.Exists(UserCredentialPath))
            {
                File.Delete(UserCredentialPath);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static string? ReadEmbedded()
    {
        var assembly = typeof(AiCredentialVault).Assembly;

        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith(EncryptedResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);

        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    /// <summary>AES-256-CBC 解密。密文格式：Base64(IV 16 字节 + 密文)。</summary>
    private static string Decrypt(string token)
    {
        var raw = Convert.FromBase64String(token);

        if (raw.Length < 17)
        {
            throw new CryptographicException("密文长度不对");
        }

        var iv = new byte[16];
        Array.Copy(raw, 0, iv, 0, 16);

        var body = new byte[raw.Length - 16];
        Array.Copy(raw, 16, body, 0, body.Length);

        var passphrase = Encoding.UTF8.GetBytes(string.Concat(PassphraseParts));

        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, passphrase, 32, Salt, Info);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(body, 0, body.Length);

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>把 JSON 反序列化成 AiCredentials（字段名大小写不敏感）。</summary>
    public static AiCredentials? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var credentials = new AiCredentials
            {
                BaseUrl = Str(root, "baseUrl") ?? string.Empty,
                Model = Str(root, "model") ?? string.Empty,
            };

            if (root.TryGetProperty("models", out var models)
                && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = Str(item, "id");

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    credentials.Models.Add(new AiModelOption
                    {
                        Id = id,
                        Label = Str(item, "label") ?? id,
                        Note = Str(item, "note") ?? string.Empty,
                        Cost = item.TryGetProperty("cost", out var costElement)
                               && costElement.TryGetInt32(out var cost) ? cost : 1,
                    });
                }
            }

            if (root.TryGetProperty("fallbacks", out var fallbacks)
                && fallbacks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in fallbacks.EnumerateArray())
                {
                    var value = item.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        credentials.Fallbacks.Add(value);
                    }
                }
            }

            if (root.TryGetProperty("keys", out var keys)
                && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in keys.EnumerateArray())
                {
                    var value = item.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        credentials.Keys.Add(value);
                    }
                }
            }

            return credentials;
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
