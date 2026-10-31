using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>排座算法与布局服务实现。</summary>
public sealed class SeatingService : ISeatingService
{
    private readonly List<Student> _students = new();

    /// <inheritdoc />
    public SeatLayout Layout { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<Student> Students => _students;

    /// <inheritdoc />
    public void LoadRoster(IReadOnlyList<Student> students)
    {
        _students.Clear();
        _students.AddRange(students);
        Layout.ClearAll();
    }

    /// <inheritdoc />
    public void UpdateLayout(int rows, int cols)
    {
        if (rows < 1 || rows > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), "行数必须在1-20之间");
        }

        if (cols < 1 || cols > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(cols), "列数必须在1-20之间");
        }

        Layout.Resize(rows, cols);
    }

    /// <inheritdoc />
    public void ArrangeAuto(SeatAlgorithm algorithm)
    {
        Layout.ClearAll();
        if (_students.Count == 0)
        {
            return;
        }

        List<Student> ordered;
        switch (algorithm)
        {
            case SeatAlgorithm.Snake:
                ordered = _students.ToList();
                FillSnake(ordered);
                break;

            case SeatAlgorithm.Grouped:
                ordered = BuildGroupedOrder();
                FillRowMajor(ordered);
                break;

            default:
                ordered = Shuffle(_students);
                FillRowMajor(ordered);
                break;
        }

        // 网格排满后，剩余学生依次安排到讲台左右侧位（先左后右）；网格未满时不占用侧位
        FillPlatformSeats(ordered);
    }

    /// <summary>把网格排满后剩余的学生依次落到讲台侧位（先左后右）。</summary>
    private void FillPlatformSeats(IReadOnlyList<Student> ordered)
    {
        var gridCapacity = Layout.Rows * Layout.Cols;
        if (ordered.Count <= gridCapacity)
        {
            return;
        }

        var platformCols = new[] { SeatLayout.PlatformSeatLeftCol, SeatLayout.PlatformSeatRightCol };
        for (var i = 0; i < SeatLayout.PlatformSeatCount && gridCapacity + i < ordered.Count; i++)
        {
            Layout.Set(SeatLayout.PlatformSeatRow, platformCols[i], ordered[gridCapacity + i].Name);
        }
    }

    /// <inheritdoc />
    public bool TryPlace(Student student, int row, int col, out SeatMoveInfo? move)
    {
        move = null;

        if (GetStudentAt(row, col) is not null)
        {
            // 目标座位已被占用，调用方应先确认清空
            return false;
        }

        if (Layout.TryFindSeat(student.Name, out var from))
        {
            move = new SeatMoveInfo(student.Name, from);
            return false;
        }

        Layout.Set(row, col, student.Name);
        return true;
    }

    /// <inheritdoc />
    public void MoveTo(Student student, int row, int col)
    {
        if (Layout.TryFindSeat(student.Name, out var from))
        {
            Layout.Clear(from.Row, from.Col);
        }

        Layout.Clear(row, col);
        Layout.Set(row, col, student.Name);
    }

    /// <inheritdoc />
    public void ClearSeat(int row, int col) => Layout.Clear(row, col);

    /// <inheritdoc />
    public bool IsSeated(string studentName) => Layout.TryFindSeat(studentName, out _);

    /// <inheritdoc />
    public string? GetStudentAt(int row, int col) => Layout.Get(row, col);

    /// <summary>蛇形填充：偶数行左→右，奇数行右→左。</summary>
    private void FillSnake(IReadOnlyList<Student> ordered)
    {
        var index = 0;
        for (var row = 0; row < Layout.Rows && index < ordered.Count; row++)
        {
            if (row % 2 == 0)
            {
                for (var col = 0; col < Layout.Cols && index < ordered.Count; col++)
                {
                    Layout.Set(row, col, ordered[index++].Name);
                }
            }
            else
            {
                for (var col = Layout.Cols - 1; col >= 0 && index < ordered.Count; col--)
                {
                    Layout.Set(row, col, ordered[index++].Name);
                }
            }
        }
    }

    /// <summary>行优先填充。</summary>
    private void FillRowMajor(IReadOnlyList<Student> ordered)
    {
        var index = 0;
        for (var row = 0; row < Layout.Rows && index < ordered.Count; row++)
        {
            for (var col = 0; col < Layout.Cols && index < ordered.Count; col++)
            {
                Layout.Set(row, col, ordered[index++].Name);
            }
        }
    }

    /// <summary>构建「好 / 中 / 差」三组交错顺序（组内随机）。</summary>
    private List<Student> BuildGroupedOrder()
    {
        var groupSize = _students.Count / 3;
        var group1 = _students.Take(groupSize).ToList();
        var group2 = _students.Skip(groupSize).Take(groupSize).ToList();
        var group3 = _students.Skip(groupSize * 2).ToList();

        ShuffleInPlace(group1);
        ShuffleInPlace(group2);
        ShuffleInPlace(group3);

        var result = new List<Student>();
        var max = Math.Max(group1.Count, Math.Max(group2.Count, group3.Count));
        for (var i = 0; i < max; i++)
        {
            if (i < group1.Count)
            {
                result.Add(group1[i]);
            }

            if (i < group2.Count)
            {
                result.Add(group2[i]);
            }

            if (i < group3.Count)
            {
                result.Add(group3[i]);
            }
        }

        return result;
    }

    private static List<Student> Shuffle(IReadOnlyList<Student> source)
    {
        var list = source.ToList();
        ShuffleInPlace(list);
        return list;
    }

    private static void ShuffleInPlace(IList<Student> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
