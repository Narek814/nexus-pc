namespace Nexus.Monitoring;

/// <summary>
/// One reading of the PC. Every value a source could not read is null, with the reason in
/// <see cref="Unavailable"/> — never a made-up number (ТЗ §5).
/// </summary>
public sealed record MetricsSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }
    public CpuMetrics Cpu { get; init; } = new();
    public MemoryMetrics Memory { get; init; } = new();
    public IReadOnlyList<GpuMetrics> Gpus { get; init; } = [];
    public IReadOnlyList<DiskMetrics> Disks { get; init; } = [];
    public NetworkMetrics Network { get; init; } = new();

    /// <summary>Key (e.g. "cpu.temperature") → human-readable reason, in Russian, shown by the app.</summary>
    public IReadOnlyDictionary<string, string> Unavailable { get; init; } = new Dictionary<string, string>();

    /// <summary>Same keys as <see cref="Unavailable"/> → stable code from <see cref="Monitoring.UnavailableCodes"/>.</summary>
    public IReadOnlyDictionary<string, string> UnavailableCodes { get; init; } = new Dictionary<string, string>();

    /// <summary>Sets both reason maps from one set.</summary>
    public MetricsSnapshot WithUnavailable(UnavailableSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return this with { Unavailable = set.Texts, UnavailableCodes = set.Codes };
    }
}

public sealed record CpuMetrics
{
    /// <summary>0–100 % across all logical processors.</summary>
    public double? Usage { get; init; }
    public int LogicalProcessors { get; init; } = Environment.ProcessorCount;
    public double? Temperature { get; init; }
    public double? ClockMhz { get; init; }
}

public sealed record MemoryMetrics
{
    public long? UsedBytes { get; init; }
    public long? TotalBytes { get; init; }

    public double? Usage => UsedBytes is { } used && TotalBytes is { } total && total > 0 ? 100.0 * used / total : null;
}

public sealed record GpuMetrics
{
    public required string Name { get; init; }
    public double? Usage { get; init; }
    public double? Temperature { get; init; }
    public long? MemoryUsedBytes { get; init; }
    public long? MemoryTotalBytes { get; init; }
    public double? CoreClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
}

public sealed record DiskMetrics
{
    public required string Name { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }

    public double Usage => TotalBytes > 0 ? 100.0 * (TotalBytes - FreeBytes) / TotalBytes : 0;
}

public sealed record NetworkMetrics
{
    public double? ReceiveBytesPerSecond { get; init; }
    public double? SendBytesPerSecond { get; init; }
}

/// <summary>Something that can read the PC's current state. Called every few seconds from one thread.</summary>
public interface IMetricsSource
{
    MetricsSnapshot Sample(DateTimeOffset now);
}
