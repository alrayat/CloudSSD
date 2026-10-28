namespace CloudSSD.Core;

/// <summary>Fixed-capacity ring buffer of numeric samples for charts. Thread-safe; O(1) append.</summary>
public sealed class MetricHistory
{
    private readonly double?[] _values;
    private int _next;
    private int _count;
    private readonly object _gate = new();

    public MetricHistory(int capacity = 300) => _values = new double?[capacity];

    public int Capacity => _values.Length;

    public void Add(double? value)
    {
        lock (_gate)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }
    }

    /// <summary>Samples oldest → newest. Null entries represent "Unavailable" periods.</summary>
    public double?[] Snapshot()
    {
        lock (_gate)
        {
            var result = new double?[_count];
            var start = (_next - _count + _values.Length) % _values.Length;
            for (int i = 0; i < _count; i++) result[i] = _values[(start + i) % _values.Length];
            return result;
        }
    }

    public double? Latest
    {
        get
        {
            lock (_gate) return _count == 0 ? null : _values[(_next - 1 + _values.Length) % _values.Length];
        }
    }

    public double? Max()
    {
        var s = Snapshot();
        double? m = null;
        foreach (var v in s) if (v is { } d && (m is null || d > m)) m = d;
        return m;
    }
}

/// <summary>Abstraction over time to keep schedulers and predictors deterministic in tests.</summary>
public interface IClock
{
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime Now => DateTime.Now;
}

public sealed class ManualClock : IClock
{
    public ManualClock(DateTime start) => Now = start;
    public DateTime Now { get; set; }
    public void Advance(TimeSpan by) => Now += by;
}
