namespace ClassManager.App.Models;

/// <summary>
/// 学生模型：姓名 + 成绩。不可变 record，便于在服务间安全传递。
/// </summary>
public sealed record Student(string Name, double Score);
