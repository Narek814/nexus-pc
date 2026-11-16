using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Monitoring;

namespace Nexus.Agent.Server;

/// <summary>Samples telemetry every 2 seconds so history exists even while no phone is connected.</summary>
internal sealed partial class MetricsLoop(MetricsSampler sampler, ILogger<MetricsLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MetricsSampler.Interval);
        do
        {
            try
            {
                sampler.SampleNow();
            }
#pragma warning disable CA1031 // A sensor failure must never stop the agent.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSampleFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metrics sample failed")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);
}

/// <summary>Wire format of metrics (docs/API.md «Мониторинг»): bytes, %, °C, MHz; null = unavailable.</summary>
internal static class MetricsJson
{
    public static JsonObject Snapshot(MetricsSnapshot s) => new()
    {
        ["ts"] = s.Timestamp.ToString("O"),
        ["cpu"] = new JsonObject
        {
            ["usage"] = Round(s.Cpu.Usage),
            ["logicalProcessors"] = s.Cpu.LogicalProcessors,
            ["temperature"] = Round(s.Cpu.Temperature),
            ["clockMhz"] = Round(s.Cpu.ClockMhz),
        },
        ["memory"] = new JsonObject
        {
            ["usage"] = Round(s.Memory.Usage),
            ["usedBytes"] = s.Memory.UsedBytes,
            ["totalBytes"] = s.Memory.TotalBytes,
        },
        ["gpus"] = new JsonArray([.. s.Gpus.Select(g => (JsonNode)new JsonObject
        {
            ["name"] = g.Name,
            ["usage"] = Round(g.Usage),
            ["temperature"] = Round(g.Temperature),
            ["memoryUsedBytes"] = g.MemoryUsedBytes,
            ["memoryTotalBytes"] = g.MemoryTotalBytes,
            ["coreClockMhz"] = Round(g.CoreClockMhz),
            ["memoryClockMhz"] = Round(g.MemoryClockMhz),
        })]),
        ["disks"] = new JsonArray([.. s.Disks.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name,
            ["totalBytes"] = d.TotalBytes,
            ["freeBytes"] = d.FreeBytes,
            ["usage"] = Round(d.Usage),
        })]),
        ["network"] = new JsonObject
        {
            ["receiveBytesPerSecond"] = Round(s.Network.ReceiveBytesPerSecond),
            ["sendBytesPerSecond"] = Round(s.Network.SendBytesPerSecond),
        },
        ["unavailable"] = new JsonObject(s.Unavailable.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
    };

    public static JsonObject History(string range, TimeSpan resolution, IReadOnlyList<MetricsPoint> points) => new()
    {
        ["range"] = range,
        ["intervalSeconds"] = (int)resolution.TotalSeconds,
        ["points"] = new JsonArray([.. points.Select(p => (JsonNode)new JsonObject
        {
            ["t"] = p.Time.ToString("O"),
            ["cpu"] = Round(p.Cpu),
            ["memory"] = Round(p.Memory),
            ["gpu"] = Round(p.Gpu),
            ["gpuTemperature"] = Round(p.GpuTemperature),
            ["rx"] = Round(p.ReceiveBytesPerSecond),
            ["tx"] = Round(p.SendBytesPerSecond),
        })]),
    };

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1) : null;
}
