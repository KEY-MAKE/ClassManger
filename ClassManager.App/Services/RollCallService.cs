using ClassManager.App.Models;

namespace ClassManager.App.Services;

/// <summary>点名引擎实现（random / sequential / uncalled_only + 历史 + 统计）。</summary>
public sealed class RollCallService : IRollCallService
{
    private readonly List<Student> _students = new();
    private readonly List<CallRecord> _history = new();
    private readonly HashSet<string> _called = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<Student> Students => _students;

    /// <inheritdoc />
    public IReadOnlyList<CallRecord> History => _history;

    /// <inheritdoc />
    public RollCallMode Mode { get; set; } = RollCallMode.Random;

    /// <inheritdoc />
    public bool IsAllCalled => _students.Count > 0 && _called.Count >= _students.Count;

    /// <inheritdoc />
    public RollCallState State => new(_students.Count, _called.Count, Math.Max(0, _students.Count - _called.Count));

    /// <inheritdoc />
    public void LoadRoster(IReadOnlyList<Student> students)
    {
        _students.Clear();
        _students.AddRange(students);
        _called.Clear();
        _history.Clear();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _called.Clear();
        _history.Clear();
    }

    /// <inheritdoc />
    public RollCallResult? Roll()
    {
        if (_students.Count == 0)
        {
            return null;
        }

        Student chosen;
        switch (Mode)
        {
            case RollCallMode.UncalledOnly:
            {
                var available = _students.Where(s => !_called.Contains(s.Name)).ToList();
                if (available.Count == 0)
                {
                    return null;
                }

                chosen = available[Random.Shared.Next(available.Count)];
                break;
            }

            case RollCallMode.Sequential:
            {
                var next = _students.FirstOrDefault(s => !_called.Contains(s.Name));
                chosen = next ?? _students[0];
                break;
            }

            default:
            {
                chosen = _students[Random.Shared.Next(_students.Count)];
                break;
            }
        }

        var alreadyCalled = _called.Contains(chosen.Name);
        var timestamp = DateTime.Now;
        if (alreadyCalled)
        {
            // 已经点过，不重复计入「已点到」
        }
        else
        {
            _called.Add(chosen.Name);
        }

        _history.Add(new CallRecord(chosen.Name, timestamp, Mode));
        return new RollCallResult(chosen, timestamp, Mode, alreadyCalled);
    }

    /// <inheritdoc />
    public string NextSequentialName()
    {
        if (_students.Count == 0)
        {
            return string.Empty;
        }

        var next = _students.FirstOrDefault(s => !_called.Contains(s.Name));
        return next?.Name ?? _students[0].Name;
    }
}
