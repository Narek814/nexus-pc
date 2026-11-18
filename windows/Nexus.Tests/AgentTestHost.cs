using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Agent.Admin;
using Nexus.Agent.Hosting;
using Nexus.Core.Power;
using Nexus.Agent.Server;
using Nexus.DevClient;

namespace Nexus.Tests;

internal sealed class FakePowerController : IPowerController
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public PowerOutcome Next { get; set; } = PowerOutcome.Success;

    public PowerOutcome CheckAvailable() => PowerOutcome.Success;

    public PowerOutcome Schedule(PowerAction action, TimeSpan delay, bool force, string reason)
    {
        Calls.Enqueue($"{action}:{(int)delay.TotalSeconds}:{force}:{reason}");
        return Next;
    }

    public PowerOutcome Sleep()
    {
        Calls.Enqueue("Sleep");
        return Next;
    }

    public PowerOutcome Cancel()
    {
        Calls.Enqueue("Cancel");
        return Next;
    }
}

/// <summary>A real agent (Kestrel + TLS + WebSocket) on 127.0.0.1 with a temporary data directory.</summary>
internal sealed class AgentTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AgentTestHost(WebApplication app, string dataDirectory, IPowerController? power, string sessionPipe)
    {
        SessionPipe = sessionPipe;
        _app = app;
        DataDirectory = dataDirectory;
        Power = power as FakePowerController;
        Services = app.Services.GetRequiredService<AgentServices>();
        Admin = app.Services.GetRequiredService<AdminService>();
    }

    public string DataDirectory { get; }
    public string SessionPipe { get; }
    public FakePowerController? Power { get; }
    public AgentServices Services { get; }
    public AdminService Admin { get; }
    public int Port => Services.Identity.Endpoints[0].Port;
    public string Fingerprint => Services.Identity.Fingerprint;
    public Guid PcId => Services.Identity.PcId;

    public static async Task<AgentTestHost> StartAsync(
        string? dataDirectory = null, IPowerController? power = null, bool fakePower = true,
        string? adminPipe = null, TimeSpan? pairingLifetime = null, TimeSpan? handshakeTimeout = null, string? trayPath = null,
        IReadOnlyList<IPAddress>? bindAddresses = null, Nexus.Monitoring.IMetricsSource? metrics = null, int? openRgbPort = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
        power ??= fakePower ? new FakePowerController() : null;
        var sessionPipe = "nexus-session-" + Guid.NewGuid().ToString("N");
        var app = AgentHost.Build([], new AgentHostOptions
        {
            DataDirectory = dataDirectory,
            Port = 0,
            BindAddresses = bindAddresses ?? [IPAddress.Loopback],
            Power = power,
            EnableAdminPipe = adminPipe is not null,
            AdminPipeName = adminPipe ?? AdminPipeServer.DefaultPipeName,
            PairingLifetime = pairingLifetime,
            HandshakeTimeout = handshakeTimeout,
            SessionPipeName = sessionPipe,
            // On Windows the agent only accepts the installed Nexus.Tray.exe; the fake tray lives in this test process.
            TrayPath = trayPath ?? Environment.ProcessPath,
            MetricsSource = metrics ?? new FakeMetricsSource(),
            OpenRgbPort = openRgbPort ?? 1, // port 1: nothing listens, OpenRGB "not running" unless a test provides one
        });
        await app.StartAsync();
        return new AgentTestHost(app, dataDirectory, power, sessionPipe);
    }

    public Task<NexusClient> ConnectAsync(string? fingerprint = null, Action<System.Net.WebSockets.ClientWebSocketOptions>? configure = null, string host = "127.0.0.1") =>
        NexusClient.ConnectAsync(host, Port, fingerprint ?? Fingerprint, CancellationToken.None, configure: configure);

    /// <summary>Runs the whole pairing ceremony: admin opens, device submits, admin approves.</summary>
    public async Task<(Guid DeviceId, ECDsa Key, JsonObject Response)> PairAsync(NexusClient client, string name = "Test iPhone", bool approve = true)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var state = new AdminConnectionState();
        var started = await Admin.HandleAsync(new JsonObject { ["op"] = "pair.start" }, state, CancellationToken.None);
        var code = started["code"]!.GetValue<string>();

        var submit = client.PairAsync(code, name, key);
        var pending = await Admin.HandleAsync(new JsonObject { ["op"] = "pair.wait" }, state, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(true, pending["ok"]?.GetValue<bool>());
        await Admin.HandleAsync(new JsonObject { ["op"] = "pair.decide", ["approve"] = approve }, state, CancellationToken.None);

        var response = await submit.WaitAsync(TimeSpan.FromSeconds(10));
        var deviceId = response["ok"]!.GetValue<bool>() ? Guid.Parse(response["payload"]!["deviceId"]!.GetValue<string>()) : Guid.Empty;
        return (deviceId, key, response);
    }

    public async Task<(NexusClient Client, Guid DeviceId, ECDsa Key)> PairedClientAsync()
    {
        var client = await ConnectAsync();
        var (deviceId, key, _) = await PairAsync(client);
        var auth = await client.AuthenticateAsync(PcId, deviceId, key, Fingerprint);
        Assert.True(auth["ok"]!.GetValue<bool>(), auth.ToJsonString());
        return (client, deviceId, key);
    }

    /// <summary>Connects a Tray (real pipe, real channel, real handler; fake desktop) and waits until the agent sees it.</summary>
    public async Task<FakeTray> ConnectTrayAsync()
    {
        var pipe = new System.IO.Pipes.NamedPipeClientStream(".", SessionPipe, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        var tray = new FakeTray(pipe);
        await AgentIntegrationTests.WaitUntil(() => Services.Trays.IsAvailable);
        return tray;
    }

    /// <summary>Connects without waiting for acceptance (for the rejection test).</summary>
    public async Task<FakeTray> ConnectTrayUncheckedAsync()
    {
        var pipe = new System.IO.Pipes.NamedPipeClientStream(".", SessionPipe, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        return new FakeTray(pipe);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

internal static class Json
{
    public static bool IsOk(this JsonObject response) => response["ok"]?.GetValue<bool>() == true;

    public static string? ErrorCode(this JsonObject response) => response["error"]?["code"]?.GetValue<string>();
}

internal sealed class FakeTray : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-faketray-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    public FakeTray(Stream pipe)
    {
        Store = new Nexus.Core.Apps.UserAppStore(_dir);
        Channel = new Nexus.Core.Session.JsonLineChannel(pipe);
        Files = new Nexus.Core.Files.SharedFolder(Path.Combine(_dir, "shared"), Path.Combine(_dir, "partial"), freeSpace: _ => long.MaxValue / 2);
        Handler = new Nexus.Core.Apps.TrayCommandHandler(Store, Desktop, files: Files, screenApprovals: new Nexus.Core.Screen.ScreenApprovals(_dir));
        _run = Channel.RunAsync(Handler.HandleAsync, _stop.Token);
    }

    public Nexus.Core.Apps.UserAppStore Store { get; }
    public FakeDesktop Desktop { get; } = new();
    public Nexus.Core.Files.SharedFolder Files { get; }
    public Nexus.Core.Apps.TrayCommandHandler Handler { get; }
    public Nexus.Core.Session.JsonLineChannel Channel { get; }

    public Task<Nexus.Core.Session.RpcResult> CallAgentAsync(string op, JsonObject? args = null) =>
        Channel.CallAsync(op, args, TimeSpan.FromSeconds(10), CancellationToken.None);

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _stop.CancelAsync();
        try
        {
            await Channel.DisposeAsync(); // flushing may fail if the agent already dropped us
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        try
        {
            await _run;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
        }
        _stop.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

/// <summary>Scripted telemetry: cpu rises by 10 each sample; GPU temperature present, CPU temperature not.</summary>
internal sealed class FakeMetricsSource : Nexus.Monitoring.IMetricsSource
{
    private int _n;

    public Nexus.Monitoring.MetricsSnapshot Sample(DateTimeOffset now)
    {
        var n = Interlocked.Increment(ref _n);
        return new Nexus.Monitoring.MetricsSnapshot
        {
            Timestamp = now,
            Cpu = new Nexus.Monitoring.CpuMetrics { Usage = Math.Min(100, n * 10) },
            Memory = new Nexus.Monitoring.MemoryMetrics { UsedBytes = 8L << 30, TotalBytes = 32L << 30 },
            Gpus = [new Nexus.Monitoring.GpuMetrics { Name = "Test GPU", Usage = 50, Temperature = 60, MemoryUsedBytes = 2L << 30, MemoryTotalBytes = 12L << 30 }],
            Disks = [new Nexus.Monitoring.DiskMetrics { Name = "C:\\", TotalBytes = 1000, FreeBytes = 250 }],
            Network = new Nexus.Monitoring.NetworkMetrics { ReceiveBytesPerSecond = 1000, SendBytesPerSecond = 500 },
            Unavailable = new Dictionary<string, string> { ["cpu.temperature"] = "no sensor driver" },
        };
    }
}
