using System.Text.Json.Nodes;
using Nexus.Core.Protocol;
using Nexus.Monitoring;

namespace Nexus.Tests;

public class MetricsSamplerTests
{
    [Fact]
    public void Fine_history_keeps_the_last_ten_minutes()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var sampler = new MetricsSampler(new FakeMetricsSource(), time);
        for (var i = 0; i < 400; i++)
        {
            sampler.SampleNow();
            time.Now += MetricsSampler.Interval;
        }
        var (resolution, points) = sampler.History("10m");
        Assert.Equal(MetricsSampler.Interval, resolution);
        Assert.Equal(300, points.Count);
        Assert.True(points[^1].Time > points[0].Time);
    }

    [Fact]
    public void Coarse_history_averages_each_minute()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var source = new ScriptedSource([10, 20, 30, 40, 50, 60]);
        var sampler = new MetricsSampler(source, time);
        // 3 samples in minute 0 (10, 20, 30), 3 in minute 1 (40, 50, 60), one in minute 2 to close minute 1.
        foreach (var offset in new[] { 0, 20, 40, 60, 80, 100, 120 })
        {
            time.Now = Shared.Now.AddSeconds(offset);
            sampler.SampleNow();
        }
        var (resolution, points) = sampler.History("24h");
        Assert.Equal(TimeSpan.FromMinutes(1), resolution);
        Assert.Equal([20.0, 50.0], points.Select(p => p.Cpu));
    }

    [Fact]
    public void Missing_values_stay_null_instead_of_being_invented()
    {
        var sampler = new MetricsSampler(new ScriptedSource([null, null]), new FixedTimeProvider(Shared.Now));
        var snapshot = sampler.SampleNow();
        Assert.Null(snapshot.Cpu.Usage);
        Assert.Null(sampler.History("10m").Points[0].Cpu);
        Assert.Null(sampler.History("10m").Points[0].Gpu);
    }

    [Fact]
    public void Unknown_range_is_rejected()
    {
        var sampler = new MetricsSampler(new FakeMetricsSource(), TimeProvider.System);
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.History("1y"));
    }

    [Fact]
    public void Portable_source_reports_what_it_cannot_read()
    {
        var source = new PortableMetricsSource();
        source.Sample(DateTimeOffset.UtcNow);
        var snapshot = source.Sample(DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Null(snapshot.Cpu.Usage);
        Assert.Contains("cpu.temperature", snapshot.Unavailable.Keys);
        Assert.NotNull(snapshot.Network.ReceiveBytesPerSecond);
    }

    [Fact]
    public void Windows_source_reads_real_values()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        using var source = new WindowsMetricsSource();
        source.Sample(DateTimeOffset.UtcNow);
        Thread.Sleep(500);
        var snapshot = source.Sample(DateTimeOffset.UtcNow);
        Assert.InRange(snapshot.Cpu.Usage!.Value, 0, 100);
        Assert.True(snapshot.Memory.TotalBytes > 0);
        Assert.InRange(snapshot.Memory.Usage!.Value, 0, 100);
        Assert.NotEmpty(snapshot.Disks);
        Assert.True(snapshot.Gpus.Count > 0 || snapshot.Unavailable.ContainsKey("gpu"), "GPU must be either read or explained");
        Assert.True(snapshot.Unavailable.ContainsKey("cpu.temperature"));
    }

    private sealed class ScriptedSource(IReadOnlyList<double?> cpu) : IMetricsSource
    {
        private int _i;

        public MetricsSnapshot Sample(DateTimeOffset now) => new()
        {
            Timestamp = now,
            Cpu = new CpuMetrics { Usage = cpu[Math.Min(_i++, cpu.Count - 1)] },
        };
    }
}

public sealed class MetricsProtocolTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task Snapshot_carries_values_and_reasons_for_missing_ones()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var response = await client.RequestAsync("metrics.snapshot");
        Assert.True(response.IsOk(), response.ToJsonString());
        var p = response["payload"]!;
        Assert.NotNull(p["cpu"]!["usage"]);
        Assert.Null(p["cpu"]!["temperature"]);
        Assert.Equal("no sensor driver", p["unavailable"]!["cpu.temperature"]!.GetValue<string>());
        Assert.Equal(25.0, p["memory"]!["usage"]!.GetValue<double>());
        Assert.Equal("Test GPU", p["gpus"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(60.0, p["gpus"]![0]!["temperature"]!.GetValue<double>());
        Assert.Equal(75.0, p["disks"]![0]!["usage"]!.GetValue<double>());
        Assert.Contains("metrics.snapshot", (await client.RequestAsync("system.hello"))["payload"]!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task History_returns_points_for_each_range()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        _host.Services.Metrics.SampleNow();
        _host.Services.Metrics.SampleNow();

        var fine = await client.RequestAsync("metrics.history", new JsonObject { ["range"] = "10m" });
        Assert.True(fine.IsOk(), fine.ToJsonString());
        Assert.Equal(2, fine["payload"]!["intervalSeconds"]!.GetValue<int>());
        Assert.True(fine["payload"]!["points"]!.AsArray().Count >= 2);
        Assert.True((await client.RequestAsync("metrics.history", new JsonObject { ["range"] = "24h" })).IsOk());
        Assert.Equal(ErrorCodes.InvalidPayload, (await client.RequestAsync("metrics.history", new JsonObject { ["range"] = "1y" })).ErrorCode());
    }

    [Fact]
    public async Task Monitoring_permission_is_required()
    {
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        _host.Services.Devices.SetPermissions(deviceId, Nexus.Core.Security.Permission.Power);
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.RequestAsync("metrics.snapshot")).ErrorCode());
    }
}
