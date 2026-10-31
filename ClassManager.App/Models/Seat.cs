namespace ClassManager.App.Models;

/// <summary>自动排座算法。</summary>
public enum SeatAlgorithm
{
    /// <summary>蛇形排列（偶数行左→右，奇数行右→左）。</summary>
    Snake,

    /// <summary>分组排列（好/中/差三组交错）。</summary>
    Grouped,

    /// <summary>随机排列。</summary>
    Random,
}

/// <summary>座位坐标（行、列均 0 基；row 0 表示「第一排」）。</summary>
public readonly record struct SeatPosition(int Row, int Col);

/// <summary>
/// 座位布局：以 (行, 列) 为键记录学生姓名；行/列均为 0 基。
/// 另持有逐道行/列间隙与分组阈值等展示参数（会话内有效，不持久化）。
/// </summary>
public sealed class SeatLayout
{
    /// <summary>默认间隙（像素）。</summary>
    public const double DefaultGap = 8d;

    /// <summary>间隙允许的最大值（像素）。</summary>
    public const double MaxGap = 400d;

    /// <summary>行数（默认 6）。</summary>
    public int Rows { get; private set; } = 6;

    /// <summary>列数（默认 8）。</summary>
    public int Cols { get; private set; } = 8;

    private readonly Dictionary<(int Row, int Col), string> _seats = new();
    private readonly List<double> _columnGaps = new();
    private readonly List<double> _rowGaps = new();

    /// <summary>全部已安排座位（只读视图）。</summary>
    public IReadOnlyDictionary<(int Row, int Col), string> Seats => _seats;

    /// <summary>
    /// 逐道列间隙：长度 = Cols - 1；ColumnGaps[i] 表示「第 i+1 列 ↔ 第 i+2 列」的水平间距（像素）。
    /// </summary>
    public IReadOnlyList<double> ColumnGaps => _columnGaps;

    /// <summary>
    /// 逐道行间隙：长度 = Rows - 1；RowGaps[i] 表示「第 i+1 排 ↔ 第 i+2 排」的垂直间距（像素）。
    /// </summary>
    public IReadOnlyList<double> RowGaps => _rowGaps;

    /// <summary>是否绘制列表头（第x列）与排标签（第x排）。</summary>
    public bool ShowHeaders { get; set; } = true;

    /// <summary>讲台侧位专用伪行号（讲台左右两侧紧贴的两个座位，可坐人）。</summary>
    public const int PlatformSeatRow = -1;

    /// <summary>讲台左侧位所在列。</summary>
    public const int PlatformSeatLeftCol = 0;

    /// <summary>讲台右侧位所在列。</summary>
    public const int PlatformSeatRightCol = 1;

    /// <summary>讲台侧位数量。</summary>
    public const int PlatformSeatCount = 2;

    /// <summary>是否讲台侧位所在行（伪坐标 row = -1）。</summary>
    public static bool IsPlatformSeat(int row) => row == PlatformSeatRow;

    /// <summary>总座位数（行 × 列 + 讲台侧位 2 个）。</summary>
    public int TotalSeats => Rows * Cols + PlatformSeatCount;

    /// <summary>已占用座位数。</summary>
    public int OccupiedSeats => _seats.Count;

    /// <summary>空座位数。</summary>
    public int EmptySeats => TotalSeats - OccupiedSeats;

    /// <summary>构造默认 6 × 8 布局（间隙全为 8px）。</summary>
    public SeatLayout() => Resize(6, 8);

    /// <summary>
    /// 调整布局尺寸：保留仍在新范围内的座位安排（越界丢弃），
    /// 并同步调整间隙列表——已有间隙保留，新增间隙填默认值 8，多余间隙删除。
    /// </summary>
    public void Resize(int rows, int cols)
    {
        Rows = rows;
        Cols = cols;

        ResizeGaps(_columnGaps, cols - 1);
        ResizeGaps(_rowGaps, rows - 1);

        var keep = new Dictionary<(int Row, int Col), string>();
        foreach (var kv in _seats)
        {
            // 讲台侧位是伪坐标（row = -1），不参与网格越界判定，必须原样保留
            if (IsPlatformSeat(kv.Key.Row)
                || (kv.Key.Row >= 0 && kv.Key.Row < rows && kv.Key.Col < cols))
            {
                keep[kv.Key] = kv.Value;
            }
        }

        _seats.Clear();
        foreach (var kv in keep)
        {
            _seats[kv.Key] = kv.Value;
        }
    }

    /// <summary>设置单道列间隙（索引越界忽略；值经 <see cref="NormalizeGap"/> 归一化）。</summary>
    public void SetColumnGap(int index, double value)
    {
        if (index >= 0 && index < _columnGaps.Count)
        {
            _columnGaps[index] = NormalizeGap(value);
        }
    }

    /// <summary>设置单道行间隙（索引越界忽略；值经 <see cref="NormalizeGap"/> 归一化）。</summary>
    public void SetRowGap(int index, double value)
    {
        if (index >= 0 && index < _rowGaps.Count)
        {
            _rowGaps[index] = NormalizeGap(value);
        }
    }

    /// <summary>把所有列间隙设为同一值（经归一化）。</summary>
    public void SetAllColumnGaps(double value)
    {
        var normalized = NormalizeGap(value);
        for (var i = 0; i < _columnGaps.Count; i++)
        {
            _columnGaps[i] = normalized;
        }
    }

    /// <summary>把所有行间隙设为同一值（经归一化）。</summary>
    public void SetAllRowGaps(double value)
    {
        var normalized = NormalizeGap(value);
        for (var i = 0; i < _rowGaps.Count; i++)
        {
            _rowGaps[i] = normalized;
        }
    }

    /// <summary>归一化间隙：NaN / 负值视为默认 8；上界夹到 400。</summary>
    public static double NormalizeGap(double value)
        => double.IsNaN(value) || value < 0d ? DefaultGap : Math.Min(value, MaxGap);

    /// <summary>读取指定座位的姓名；空座位返回 null。</summary>
    public string? Get(int row, int col) => _seats.TryGetValue((row, col), out var name) ? name : null;

    /// <summary>安排学生到指定座位（覆盖已有）。</summary>
    public void Set(int row, int col, string studentName) => _seats[(row, col)] = studentName;

    /// <summary>清空指定座位。</summary>
    public void Clear(int row, int col) => _seats.Remove((row, col));

    /// <summary>查找某个学生的座位。</summary>
    public bool TryFindSeat(string studentName, out (int Row, int Col) seat)
    {
        foreach (var kv in _seats)
        {
            if (string.Equals(kv.Value, studentName, StringComparison.Ordinal))
            {
                seat = kv.Key;
                return true;
            }
        }

        seat = default;
        return false;
    }

    /// <summary>清空全部座位安排。</summary>
    public void ClearAll() => _seats.Clear();

    /// <summary>调整间隙列表长度：保留已有值，不足填默认值，多余删除。</summary>
    private static void ResizeGaps(List<double> gaps, int count)
    {
        count = Math.Max(0, count);
        while (gaps.Count < count)
        {
            gaps.Add(DefaultGap);
        }

        if (gaps.Count > count)
        {
            gaps.RemoveRange(count, gaps.Count - count);
        }
    }
}

/// <summary>座位移动信息：学生姓名 + 原座位（若原本未排座则为 null）。</summary>
public sealed record SeatMoveInfo(string StudentName, (int Row, int Col)? From);
