using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Agent.Admin;
using Nexus.Agent.Hosting;
using Nexus.Agent.Power;
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

    private AgentTestHost(WebApplication app, string dataDirectory, IPowerController? power)
    {
        _app = app;
        DataDirectory = dataDirectory;
        Power = power as FakePowerController;
        Services = app.Services.GetRequiredService<AgentServices>();
        Admin = app.Services.GetRequiredService<AdminService>();
    }

    public string DataDirectory { get; }
    public FakePowerController? Power { get; }
    public AgentServices Services { get; }
    public AdminService Admin { get; }
    public int Port => Services.Identity.Endpoints[0].Port;
    public string Fingerprint => Services.Identity.Fingerprint;
    public Guid PcId => Services.Identity.PcId;

    public static async Task<AgentTestHost> StartAsync(
        string? dataDirectory = null, IPowerController? power = null, bool fakePower = true,
        string? adminPipe = null, TimeSpan? pairingLifetime = null, TimeSpan? handshakeTimeout = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
        power ??= fakePower ? new FakePowerController() : null;
        var app = AgentHost.Build([], new AgentHostOptions
        {
            DataDirectory = dataDirectory,
            Port = 0,
            BindAddresses = [IPAddress.Loopback],
            Power = power,
            EnableAdminPipe = adminPipe is not null,
            AdminPipeName = adminPipe ?? AdminPipeServer.DefaultPipeName,
            PairingLifetime = pairingLifetime,
            HandshakeTimeout = handshakeTimeout,
        });
        await app.StartAsync();
        return new AgentTestHost(app, dataDirectory, power);
    }

    public Task<NexusClient> ConnectAsync(string? fingerprint = null, Action<System.Net.WebSockets.ClientWebSocketOptions>? configure = null) =>
        NexusClient.ConnectAsync("127.0.0.1", Port, fingerprint ?? Fingerprint, CancellationToken.None, configure: configure);

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
