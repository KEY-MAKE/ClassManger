using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>排座服务接口。</summary>
public interface ISeatingService
{
    /// <summary>当前座位布局。</summary>
    SeatLayout Layout { get; }

    /// <summary>当前名单。</summary>
    IReadOnlyList<Student> Students { get; }

    /// <summary>载入名单并清空座位安排。</summary>
    void LoadRoster(IReadOnlyList<Student> students);

    /// <summary>更新布局尺寸（校验 1–20 / 1–20），保留范围内安排，并调整间隙列表。</summary>
    void UpdateLayout(int rows, int cols);

    /// <summary>清空后按算法自动填充。</summary>
    void ArrangeAuto(SeatAlgorithm algorithm);

    /// <summary>尝试放置学生；返回 true 表示已放置，false 表示需要调用方二次确认（move 非空表示学生已在别处）。</summary>
    bool TryPlace(Student student, int row, int col, out SeatMoveInfo? move);

    /// <summary>强制移动学生到指定座位。</summary>
    void MoveTo(Student student, int row, int col);

    /// <summary>清空指定座位。</summary>
    void ClearSeat(int row, int col);

    /// <summary>学生是否已排座。</summary>
    bool IsSeated(string studentName);

    /// <summary>读取指定座位上的学生；空座位返回 null。</summary>
    string? GetStudentAt(int row, int col);
}
