using System.IO;
using System.Text.Json;
using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>
/// 上次导入名单的本地快照。
/// <para>
/// 需求：每次启动都要重新选成绩单太麻烦。导入成功后把名单落盘，
/// 启动时读回来，点名/座位直接可用。
/// </para>
/// <para>
/// 注意：这里存的是**导入结果**，不是用户数据源。源文件删了也不影响 ——
/// 所以用 <see cref="StoredRoster"/> 完整复制一份姓名+成绩，而不是只存文件路径。
/// </para>
/// </summary>
public static class RosterStore
{
    private const string FileName = "roster.json";

    /// <summary>快照文件路径：%AppData%\ClassManager\roster.json。</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClassManager",
        FileName);

    /// <summary>落盘（失败不抛异常 —— 存不下最多下次重新导入，不该影响当前这轮使用）。</summary>
    public static void Save(ImportResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stored = new StoredRoster
            {
                ClassName = result.ClassName,
                SourceFile = result.FilePath,
                ImportedAt = DateTime.Now,
                Students = result.Students.Select(s => new StoredStudent
                {
                    Name = s.Name,
                    Score = s.Score,
                }).ToList(),
            };

            var json = JsonSerializer.Serialize(stored, SettingsService.JsonOptions);
            var temp = FilePath + ".tmp";

            // 先写临时文件再替换：避免写到一半断电，下次读到半个 JSON 直接丢弃整份名单
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // 忽略：快照只是便利功能
        }
    }

    /// <summary>读回上次的名单；没有 / 损坏 / 为空时返回 null。</summary>
    public static ImportResult? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath);
            var stored = JsonSerializer.Deserialize<StoredRoster>(json, SettingsService.JsonOptions);

            if (stored?.Students is null || stored.Students.Count == 0)
            {
                return null;
            }

            var students = stored.Students
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new Student(s.Name.Trim(), s.Score))
                .ToList();

            if (students.Count == 0)
            {
                return null;
            }

            return new ImportResult(
                students,
                stored.SourceFile ?? string.Empty,
                string.IsNullOrWhiteSpace(stored.ClassName) ? "上次导入" : stored.ClassName,
                null,
                null,
                false,
                false,
                null);
        }
        catch
        {
            // 文件损坏 / 权限问题 → 当作没有快照，而不是让启动崩掉
            return null;
        }
    }

    /// <summary>删除快照（用户主动清空名单时调用）。</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>快照的落盘结构。</summary>
    private sealed class StoredRoster
    {
        public string? ClassName { get; set; }

        public string? SourceFile { get; set; }

        public DateTime ImportedAt { get; set; }

        public List<StoredStudent> Students { get; set; } = new();
    }

    private sealed class StoredStudent
    {
        public string Name { get; set; } = string.Empty;

        public double Score { get; set; }
    }
}
