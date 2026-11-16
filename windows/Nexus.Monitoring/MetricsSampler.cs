namespace Nexus.Monitoring;

/// <summary>One point of history: the numbers charts need, null when unavailable.</summary>
public sealed record MetricsPoint(
    DateTimeOffset Time,
    double? Cpu,
    double? Memory,
    double? Gpu,
    double? GpuTemperature,
    double? ReceiveBytesPerSecond,
    double? SendBytesPerSecond);

/// <summary>
/// Samples every <see cref="Interval"/> and keeps history in memory: 10 minutes at full resolution
/// and 24 hours as 1-minute averages. History restarts with the agent (persistent history is P6).
/// </summary>
public sealed class MetricsSampler
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const int FinePoints = 300;      // 10 min at 2 s
    private const int CoarsePoints = 1440;   // 24 h at 1 min

    private readonly IMetricsSource _source;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Lock _sampleLock = new(); // sources keep state between samples: one at a time
    private readonly Queue<MetricsPoint> _fine = new();
    private readonly Queue<MetricsPoint> _coarse = new();
    private readonly List<MetricsPoint> _currentMinute = [];
    private MetricsSnapshot? _latest;

    public MetricsSampler(IMetricsSource source, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);
        _source = source;
        _time = time;
    }

    public MetricsSnapshot? Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>Takes one sample now. Called by the agent's background loop (and directly by tests).</summary>
    public MetricsSnapshot SampleNow()
    {
        MetricsSnapshot snapshot;
        lock (_sampleLock)
        {
            snapshot = _source.Sample(_time.GetUtcNow());
        }
        var point = new MetricsPoint(
            snapshot.Timestamp,
            snapshot.Cpu.Usage,
            snapshot.Memory.Usage,
            snapshot.Gpus.Count > 0 ? snapshot.Gpus.Max(g => g.Usage) : null,
            snapshot.Gpus.Count > 0 ? snapshot.Gpus.Max(g => g.Temperature) : null,
            snapshot.Network.ReceiveBytesPerSecond,
            snapshot.Network.SendBytesPerSecond);

        lock (_lock)
        {
            _latest = snapshot;
            Push(_fine, point, FinePoints);
            if (_currentMinute.Count > 0 && Minute(_currentMinute[0].Time) != Minute(point.Time))
            {
                Push(_coarse, Average(_currentMinute), CoarsePoints);
                _currentMinute.Clear();
            }
            _currentMinute.Add(point);
        }
        return snapshot;
    }

    /// <summary>"10m": 2-second points; "1h" and "24h": 1-minute averages.</summary>
    public (TimeSpan Resolution, IReadOnlyList<MetricsPoint> Points) History(string range)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            return range switch
            {
                "10m" => (Interval, [.. _fine]),
                "1h" => (TimeSpan.FromMinutes(1), [.. _coarse.Where(p => p.Time >= now.AddHours(-1))]),
                "24h" => (TimeSpan.FromMinutes(1), [.. _coarse]),
                _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Unknown history range."),
            };
        }
    }

    private static void Push(Queue<MetricsPoint> queue, MetricsPoint point, int capacity)
    {
        queue.Enqueue(point);
        while (queue.Count > capacity)
        {
            queue.Dequeue();
        }
    }

    private static DateTimeOffset Minute(DateTimeOffset time) =>
        new(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Offset);

    private static MetricsPoint Average(List<MetricsPoint> points)
    {
        static double? Avg(IEnumerable<double?> values)
        {
            var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return present.Count > 0 ? present.Average() : null;
        }

        return new MetricsPoint(
            Minute(points[0].Time),
            Avg(points.Select(p => p.Cpu)),
            Avg(points.Select(p => p.Memory)),
            Avg(points.Select(p => p.Gpu)),
            Avg(points.Select(p => p.GpuTemperature)),
            Avg(points.Select(p => p.ReceiveBytesPerSecond)),
            Avg(points.Select(p => p.SendBytesPerSecond)));
    }
}
