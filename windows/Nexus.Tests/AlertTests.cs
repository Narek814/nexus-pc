using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nexus.Core.Protocol;
using Nexus.Monitoring;
using Nexus.Notifications;

namespace Nexus.Tests;

public class AlertEngineTests
{
    private static MetricsSnapshot Gpu(double? temperature, DateTimeOffset at) => new()
    {
        Timestamp = at,
        Gpus = [new GpuMetrics { Name = "RTX", Temperature = temperature }],
    };

    private static List<AlertEvent> Run(AlertEngine engine, FixedTimeProvider time, AlertSettings settings, params (int Seconds, double? Temp)[] samples)
    {
        var events = new List<AlertEvent>();
        foreach (var (seconds, temp) in samples)
        {
            time.Now = Shared.Now.AddSeconds(seconds);
            events.AddRange(engine.Evaluate(Gpu(temp, time.Now), settings, "PC"));
        }
        return events;
    }

    [Fact]
    public void Gpu_temperature_must_hold_for_30_seconds()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var engine = new AlertEngine(time);
        var settings = new AlertSettings { GpuTemperatureC = 85 };

        // A 20-second spike is ignored.
        Assert.Empty(Run(engine, time, settings, (0, 90), (10, 91), (20, 90), (22, 70)));
        // 30 seconds above the threshold fires exactly once.
        var events = Run(engine, time, settings, (100, 90), (110, 90), (130, 92), (140, 95), (160, 95));
        var alert = Assert.Single(events);
        Assert.Contains("92°C", alert.Message, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void Re_arms_only_after_a_clear_drop_and_respects_the_cooldown()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var engine = new AlertEngine(time);
        var settings = new AlertSettings { GpuTemperatureC = 85, CooldownMinutes = 30 };

        Assert.Single(Run(engine, time, settings, (0, 90), (30, 90)));
        // Hovering in the margin (81–84) does not re-arm; still hot later does not repeat.
        Assert.Empty(Run(engine, time, settings, (60, 83), (90, 90), (130, 90)));
        // A clear drop re-arms, but within 30 minutes the cooldown still blocks.
        Assert.Empty(Run(engine, time, settings, (200, 70), (300, 90), (340, 90)));
        // After the cooldown, a new episode alerts again.
        Assert.Single(Run(engine, time, settings, (2000, 70), (2100, 90), (2140, 90)));
    }

    [Fact]
    public void Hovering_just_below_the_threshold_does_not_repeat_even_after_the_cooldown()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var engine = new AlertEngine(time);
        var settings = new AlertSettings { GpuTemperatureC = 85, CooldownMinutes = 5 };

        Assert.Single(Run(engine, time, settings, (0, 90), (30, 90)));
        // Cooldown (5 min) is over at 330 s; the value only dipped to 83 (inside the 5° margin).
        Assert.Empty(Run(engine, time, settings, (400, 83), (410, 90), (450, 90), (500, 84), (510, 90), (560, 90)));
    }

    [Fact]
    public void Unavailable_values_never_alert()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var engine = new AlertEngine(time);
        Assert.Empty(Run(engine, time, new AlertSettings(), (0, null), (100, null)));
        var snapshot = new MetricsSnapshot { Timestamp = time.Now, Cpu = new CpuMetrics { Usage = null } };
        time.Now = time.Now.AddHours(1);
        Assert.Empty(engine.Evaluate(snapshot, new AlertSettings(), "PC"));
    }

    [Fact]
    public void Low_disk_alerts_immediately_and_disabled_rules_stay_silent()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var engine = new AlertEngine(time);
        var snapshot = new MetricsSnapshot
        {
            Timestamp = time.Now,
            Disks = [new DiskMetrics { Name = "C:\\\\", TotalBytes = 100L << 30, FreeBytes = 5L << 30 }],
        };
        Assert.Empty(new AlertEngine(time).Evaluate(snapshot, new AlertSettings { DiskFreeEnabled = false }, "PC"));
        var alert = Assert.Single(engine.Evaluate(snapshot, new AlertSettings { DiskFreePercent = 10 }, "PC"));
        Assert.Contains("5.0 ГБ", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sustained_cpu_load_needs_five_minutes()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var engine = new AlertEngine(time);
        var settings = new AlertSettings { CpuUsagePercent = 95 };
        MetricsSnapshot Cpu(double usage) => new() { Timestamp = time.Now, Cpu = new CpuMetrics { Usage = usage } };

        Assert.Empty(engine.Evaluate(Cpu(99), settings, "PC"));
        time.Now = Shared.Now.AddMinutes(4);
        Assert.Empty(engine.Evaluate(Cpu(99), settings, "PC"));
        time.Now = Shared.Now.AddMinutes(5);
        Assert.Single(engine.Evaluate(Cpu(99), settings, "PC"));
    }

    [Fact]
    public void Settings_validation_and_topic()
    {
        Assert.Null(new AlertSettings().Validate());
        Assert.NotNull(new AlertSettings { GpuTemperatureC = 200 }.Validate());
        Assert.NotNull(new AlertSettings { NtfyServer = "http://ntfy.sh" }.Validate());
        Assert.NotNull(new AlertSettings { NtfyServer = "https://ntfy.sh/topic" }.Validate());
        var topic = AlertSettings.NewTopic();
        Assert.Matches("^nexuspc-[0-9a-f]{32}$", topic);
        Assert.NotEqual(topic, AlertSettings.NewTopic());
    }
}

public sealed class PowerLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-life-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Clean_stop_then_start_is_not_unexpected()
    {
        var path = Path.Combine(_dir, "running.marker");
        var life = new PowerLifecycle(path, new FixedTimeProvider(Shared.Now));
        Assert.Null(life.Start());
        life.CleanStop();
        Assert.Null(new PowerLifecycle(path, TimeProvider.System).Start());
    }

    [Fact]
    public void Missing_clean_stop_is_reported_with_the_last_heartbeat()
    {
        var path = Path.Combine(_dir, "running.marker");
        var time = new FixedTimeProvider(Shared.Now);
        var life = new PowerLifecycle(path, time);
        life.Start();
        time.Now = Shared.Now.AddMinutes(7);
        life.Heartbeat();
        // Power cut: no CleanStop.
        Assert.Equal(Shared.Now.AddMinutes(7), new PowerLifecycle(path, TimeProvider.System).Start());
    }
}

public class NtfyNotifierTests
{
    [Fact]
    public async Task Posts_title_priority_and_message_to_server_slash_topic()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = listener.GetContextAsync();

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var settings = new AlertSettings { NtfyEnabled = true, NtfyServer = $"http://127.0.0.1:{port}", NtfyTopic = "nexuspc-test" };
        var send = new NtfyNotifier(http).SendAsync(new AlertEvent("k", "PC: видеокарта горячая", "RTX: 90°C", AlertSeverity.Warning), settings, CancellationToken.None);

        var context = await received.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("POST", context.Request.HttpMethod);
        Assert.Equal("/nexuspc-test", context.Request.Url!.AbsolutePath);
        Assert.Equal("high", context.Request.Headers["Priority"]);
        var title = context.Request.Headers["Title"]!;
        Assert.Equal("PC: видеокарта горячая", Encoding.UTF8.GetString(Convert.FromBase64String(title["=?UTF-8?B?".Length..^2])));
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            Assert.Equal("RTX: 90°C", await reader.ReadToEndAsync());
        }
        context.Response.StatusCode = 200;
        context.Response.Close();
        Assert.True(await send);
    }

    [Fact]
    public async Task Disabled_or_unreachable_is_false_not_an_exception()
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var notifier = new NtfyNotifier(http);
        var alert = new AlertEvent("k", "t", "m", AlertSeverity.Info);
        Assert.False(await notifier.SendAsync(alert, new AlertSettings { NtfyEnabled = false, NtfyTopic = "x" }, CancellationToken.None));
        Assert.False(await notifier.SendAsync(alert, new AlertSettings { NtfyEnabled = true, NtfyServer = $"http://127.0.0.1:{GetFreePort()}", NtfyTopic = "x" }, CancellationToken.None));
    }

    private static int GetFreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}

public sealed class AlertsProtocolTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;
    private readonly FakeNotifier _notifier = new();

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync(notifier: _notifier);

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task Ntfy_is_off_by_default_and_gets_a_random_topic_when_enabled()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var initial = await client.RequestAsync("alerts.get");
        Assert.False(initial["payload"]!["ntfy"]!["enabled"]!.GetValue<bool>());
        Assert.Null(initial["payload"]!["ntfy"]!["topic"]);
        Assert.Equal(85, initial["payload"]!["gpuTemperatureC"]!.GetValue<int>());

        var enabled = await client.RequestAsync("alerts.set", new JsonObject { ["ntfyEnabled"] = true, ["gpuTemperatureC"] = 80 });
        Assert.True(enabled.IsOk(), enabled.ToJsonString());
        Assert.Matches("^nexuspc-[0-9a-f]{32}$", enabled["payload"]!["ntfy"]!["topic"]!.GetValue<string>());
        Assert.Equal(80, enabled["payload"]!["gpuTemperatureC"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(_host.DataDirectory, "alerts.json")));
    }

    [Fact]
    public async Task Out_of_range_threshold_is_refused_and_nothing_changes()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        Assert.Equal(ErrorCodes.InvalidPayload, (await client.RequestAsync("alerts.set", new JsonObject { ["gpuTemperatureC"] = 300 })).ErrorCode());
        Assert.Equal(85, (await client.RequestAsync("alerts.get"))["payload"]!["gpuTemperatureC"]!.GetValue<int>());
    }

    [Fact]
    public async Task Test_notification_goes_through_the_notifier()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        await client.RequestAsync("alerts.set", new JsonObject { ["ntfyEnabled"] = true });
        var test = await client.RequestAsync("alerts.test");
        Assert.True(test["payload"]!["delivered"]!.GetValue<bool>());
        Assert.Contains(_notifier.Sent, a => a.Key == "test");
    }

    [Fact]
    public async Task Power_on_event_is_raised_at_start()
    {
        await AgentIntegrationTests.WaitUntil(() => _notifier.Sent.Any(a => a.Key == "power.on"));
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        var recent = (await client.RequestAsync("alerts.get"))["payload"]!["recent"]!.AsArray();
        Assert.Contains(recent, r => r!["title"]!.GetValue<string>().Contains("включился", StringComparison.Ordinal));
    }
}
