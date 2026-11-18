using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexus.RGB;

namespace Nexus.Tests;

/// <summary>
/// A stand-in OpenRGB SDK server (protocol 0). Its encoding follows the independent openrgb-python
/// client (utils.py pack methods), not Nexus.RGB, so the two implementations check each other.
/// </summary>
internal sealed class FakeOpenRgbServer : IAsyncDisposable
{
    public sealed record FakeMode(string Name, uint ColorMode, int Colors);
    public sealed record FakeZone(string Name, int Type, int Leds, (int H, int W)? Matrix = null);
    public sealed record FakeDevice(string Name, int Type, FakeMode[] Modes, FakeZone[] Zones);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly FakeDevice[] _devices;
    private readonly CancellationTokenSource _stop = new();

    public FakeOpenRgbServer(params FakeDevice[] devices)
    {
        _devices = devices;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<(uint Device, uint Packet, byte[] Data)> Received { get; } = new();
    public bool SendNotificationFirst { get; set; }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[16];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    Assert.Equal("ORGB", Encoding.ASCII.GetString(header, 0, 4));
                    var device = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    var packet = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    var data = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))];
                    await stream.ReadExactlyAsync(data, _stop.Token);
                    Received.Enqueue((device, packet, data));
                    if (SendNotificationFirst)
                    {
                        await stream.WriteAsync(Packet(0, 100, []), _stop.Token); // DEVICE_LIST_UPDATED
                    }
                    switch (packet)
                    {
                        case 0:
                            await stream.WriteAsync(Packet(0, 0, BitConverter.GetBytes((uint)_devices.Length)), _stop.Token);
                            break;
                        case 1:
                            await stream.WriteAsync(Packet(device, 1, Controller(_devices[device])), _stop.Token);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException)
            {
            }
        }
    }

    private static byte[] Packet(uint device, uint id, byte[] data)
    {
        var p = new byte[16 + data.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(p, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), (uint)data.Length);
        data.CopyTo(p, 16);
        return p;
    }

    // openrgb-python pack_string: uint16 (len + 1), ASCII bytes, NUL.
    private static void Str(BinaryWriter w, string s)
    {
        w.Write((ushort)(s.Length + 1));
        w.Write(Encoding.ASCII.GetBytes(s));
        w.Write((byte)0);
    }

    // openrgb-python ControllerData.pack (version 0: no vendor, no brightness, no segments).
    private static byte[] Controller(FakeDevice d)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body))
        {
            w.Write(d.Type);
            Str(w, d.Name);
            Str(w, "description");
            Str(w, "1.0");
            Str(w, "serial");
            Str(w, "HID: /dev/x");
            w.Write((ushort)d.Modes.Length);
            w.Write(0); // active mode
            foreach (var m in d.Modes)
            {
                Str(w, m.Name);
                w.Write(0);   // value
                w.Write(0u);  // flags
                w.Write(0u);  // speed_min
                w.Write(0u);  // speed_max
                w.Write(1u);  // colors_min
                w.Write(1u);  // colors_max
                w.Write(0u);  // speed
                w.Write(0u);  // direction
                w.Write(m.ColorMode);
                w.Write((ushort)m.Colors);
                for (var c = 0; c < m.Colors; c++)
                {
                    w.Write(new byte[] { 1, 2, 3, 0 });
                }
            }
            w.Write((ushort)d.Zones.Length);
            foreach (var z in d.Zones)
            {
                Str(w, z.Name);
                w.Write(z.Type);
                w.Write((uint)z.Leds);
                w.Write((uint)z.Leds);
                w.Write((uint)z.Leds);
                if (z.Matrix is { } m)
                {
                    w.Write((ushort)(m.H * m.W)); // openrgb-python writes the entry count here
                    w.Write((uint)m.H);
                    w.Write((uint)m.W);
                    for (var i = 0; i < m.H * m.W; i++)
                    {
                        w.Write(i < z.Leds ? (uint)i : 0xFFFFFFFFu);
                    }
                }
                else
                {
                    w.Write((ushort)0);
                }
            }
            var leds = d.Zones.Sum(z => z.Leds);
            w.Write((ushort)leds);
            for (var i = 0; i < leds; i++)
            {
                Str(w, $"LED {i}");
                w.Write((uint)i);
            }
            w.Write((ushort)leds);
            for (var i = 0; i < leds; i++)
            {
                w.Write(0u);
            }
        }
        var bytes = body.ToArray();
        var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)result.Length);
        bytes.CopyTo(result, 4);
        return result;
    }

    public static FakeOpenRgbServer OwnersPc() => new(
        new FakeDevice("ASUS PRIME B660M-A WIFI D4", 0,
            [new("Direct", 1, 0), new("Off", 0, 0), new("Static", 2, 1), new("Breathing", 2, 1), new("Rainbow", 0, 0), new("Strobe", 2, 1)],
            [new("Aura Addressable 1", 1, 30), new("Aura Mainboard", 0, 1)]),
        new FakeDevice("Redragon M914 NIX", 6,
            [new("Direct", 1, 0), new("Breathing", 2, 1), new("Rainbow", 0, 0)],
            [new("Mouse", 2, 3, (2, 2))]));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _stop.Dispose();
    }
}

public class OpenRgbClientTests
{
    [Fact]
    public async Task Lists_the_owners_devices_with_modes_and_led_counts()
    {
        await using var server = FakeOpenRgbServer.OwnersPc();
        await using var client = await OpenRgbClient.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        var devices = await client.ListDevicesAsync(CancellationToken.None);

        Assert.Equal(["ASUS PRIME B660M-A WIFI D4", "Redragon M914 NIX"], devices.Select(d => d.Name));
        Assert.Equal(("motherboard", 31), (devices[0].Type, devices[0].LedCount));
        Assert.Equal(("mouse", 3), (devices[1].Type, devices[1].LedCount)); // matrix zone parsed
        Assert.Equal(["Direct", "Off", "Static", "Breathing", "Rainbow", "Strobe"], devices[0].Modes.Select(m => m.Name));
        Assert.Contains(server.Received, p => p.Packet == 50 && Encoding.ASCII.GetString(p.Data) == "Nexus PC\0");
    }

    [Fact]
    public async Task Static_color_sets_custom_mode_then_every_led()
    {
        await using var server = FakeOpenRgbServer.OwnersPc();
        await using var client = await OpenRgbClient.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);
        var mouse = (await client.ListDevicesAsync(CancellationToken.None))[1];

        await client.SetStaticAsync(mouse, new RgbColor(0x9B, 0x5C, 0xFF), CancellationToken.None);
        await AgentIntegrationTests.WaitUntil(() => server.Received.Any(p => p.Packet == 1050));

        var packets = server.Received.Where(p => p.Device == 1 && p.Packet >= 1000).ToList();
        Assert.Equal([1100u, 1050u], packets.Select(p => p.Packet));
        var data = packets[1].Data;
        Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)));
        Assert.Equal(new byte[] { 0x9B, 0x5C, 0xFF, 0, 0x9B, 0x5C, 0xFF, 0, 0x9B, 0x5C, 0xFF, 0 }, data[6..]);
    }

    [Fact]
    public async Task Hardware_mode_is_sent_back_with_the_chosen_color()
    {
        await using var server = FakeOpenRgbServer.OwnersPc();
        await using var client = await OpenRgbClient.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);
        var board = (await client.ListDevicesAsync(CancellationToken.None))[0];
        var breathing = board.Modes.Single(m => m.Name == "Breathing");

        await client.SetModeAsync(board, breathing, new RgbColor(255, 0, 0), CancellationToken.None);
        await AgentIntegrationTests.WaitUntil(() => server.Received.Any(p => p.Packet == 1101));

        var data = server.Received.Single(p => p.Packet == 1101).Data;
        Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)));
        Assert.Equal("Breathing", Encoding.ASCII.GetString(data, 10, 9));
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, data[^4..]);
    }

    [Fact]
    public async Task Notifications_between_responses_are_skipped()
    {
        await using var server = FakeOpenRgbServer.OwnersPc();
        server.SendNotificationFirst = true;
        await using var client = await OpenRgbClient.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);
        Assert.Equal(2, (await client.ListDevicesAsync(CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData("#9B5CFF", 0x9B, 0x5C, 0xFF)]
    [InlineData("#000000", 0, 0, 0)]
    public void Color_parsing(string text, byte r, byte g, byte b)
    {
        Assert.True(RgbColor.TryParse(text, out var color));
        Assert.Equal(new RgbColor(r, g, b), color);
        Assert.Equal(text, color.ToString());
        Assert.False(RgbColor.TryParse("red", out _));
        Assert.Equal(new RgbColor(50, 0, 128), new RgbColor(100, 0, 255).Scaled(50));
    }
}

public sealed class RgbProtocolTests : IAsyncLifetime
{
    private FakeOpenRgbServer _openRgb = null!;
    private AgentTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _openRgb = FakeOpenRgbServer.OwnersPc();
        _host = await AgentTestHost.StartAsync(openRgbPort: _openRgb.Port);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _openRgb.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task New_pairings_get_the_rgb_permission_and_see_the_devices()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var status = await client.RequestAsync("rgb.status");
        Assert.True(status.IsOk(), status.ToJsonString());
        Assert.True(status["payload"]!["available"]!.GetValue<bool>());
        Assert.Equal(["ASUS PRIME B660M-A WIFI D4", "Redragon M914 NIX"], status["payload"]!["devices"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Static_color_reaches_every_device_with_brightness_applied()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var result = await client.RequestAsync("rgb.apply", new System.Text.Json.Nodes.JsonObject { ["color"] = "#FF8000", ["brightness"] = 50, ["effect"] = "static" });
        Assert.True(result.IsOk(), result.ToJsonString());
        Assert.Equal(2, result["payload"]!["applied"]!.GetValue<int>());
        await AgentIntegrationTests.WaitUntil(() => _openRgb.Received.Count(p => p.Packet == 1050) == 2);
        var leds = _openRgb.Received.Where(p => p.Packet == 1050).Select(p => p.Data).ToList();
        Assert.All(leds, d => Assert.Equal(new byte[] { 128, 64, 0, 0 }, d[6..10]));
    }

    [Fact]
    public async Task Effect_missing_on_a_device_is_reported_for_that_device_only()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var result = await client.RequestAsync("rgb.apply", new System.Text.Json.Nodes.JsonObject { ["color"] = "#FF0000", ["brightness"] = 100, ["effect"] = "strobe" });
        var items = result["payload"]!["results"]!.AsArray();
        Assert.True(items[0]!["ok"]!.GetValue<bool>());   // the board has "Strobe"
        Assert.False(items[1]!["ok"]!.GetValue<bool>());  // the mouse does not
        await AgentIntegrationTests.WaitUntil(() => _openRgb.Received.Any(p => p.Packet == 1101 && p.Device == 0));
    }

    [Fact]
    public async Task Lighting_profile_switches_with_the_app_profile()
    {
        await using var tray = await _host.ConnectTrayAsync();
        tray.Store.AddApp("Steam", Nexus.Core.Apps.AppLaunchKind.Executable, "steam.exe", false, null);
        tray.Store.UpsertProfile(new Nexus.Core.Apps.ProfileDefinition("gaming", "Gaming", ["steam"]));
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var upsert = await client.RequestAsync("rgb.profiles.upsert", new System.Text.Json.Nodes.JsonObject
        {
            ["profile"] = new System.Text.Json.Nodes.JsonObject { ["id"] = "gaming", ["name"] = "Gaming", ["color"] = "#FF0000", ["brightness"] = 100, ["effect"] = "static" },
        });
        Assert.True(upsert.IsOk(), upsert.ToJsonString());

        var run = await client.RequestAsync("profiles.run", new System.Text.Json.Nodes.JsonObject { ["profileId"] = "gaming" });
        Assert.True(run.IsOk(), run.ToJsonString());
        Assert.Equal("applied", run["payload"]!["lighting"]!.GetValue<string>());
        Assert.Equal(1, run["payload"]!["started"]!.GetValue<int>());
        await AgentIntegrationTests.WaitUntil(() => _openRgb.Received.Count(p => p.Packet == 1050) >= 2);
    }
}

public sealed class RgbPermissionTests : IAsyncLifetime
{
    private FakeOpenRgbServer _openRgb = null!;
    private AgentTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _openRgb = FakeOpenRgbServer.OwnersPc();
        _host = await AgentTestHost.StartAsync(openRgbPort: _openRgb.Port);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _openRgb.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task App_profile_does_not_touch_lighting_for_a_device_without_rgb()
    {
        await using var tray = await _host.ConnectTrayAsync();
        tray.Store.AddApp("Steam", Nexus.Core.Apps.AppLaunchKind.Executable, "steam.exe", false, null);
        tray.Store.UpsertProfile(new Nexus.Core.Apps.ProfileDefinition("gaming", "Gaming", ["steam"]));
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        var upsert = await client.RequestAsync("rgb.profiles.upsert", new System.Text.Json.Nodes.JsonObject
        {
            ["profile"] = new System.Text.Json.Nodes.JsonObject { ["id"] = "gaming", ["name"] = "Gaming", ["color"] = "#FF0000", ["brightness"] = 100, ["effect"] = "static" },
        });
        Assert.True(upsert.IsOk(), upsert.ToJsonString());

        _host.Services.Devices.SetPermissions(deviceId, Nexus.Core.Security.Permission.Apps);
        var run = await client.RequestAsync("profiles.run", new System.Text.Json.Nodes.JsonObject { ["profileId"] = "gaming" });
        Assert.True(run.IsOk(), run.ToJsonString());
        Assert.Null(run["payload"]!["lighting"]);
        Assert.DoesNotContain(_openRgb.Received, p => p.Packet == 1050);
    }
}

public sealed class RgbUnavailableTests
{
    [Fact]
    public async Task Without_openrgb_status_explains_and_apply_fails_cleanly()
    {
        await using var host = await AgentTestHost.StartAsync();
        try
        {
            var (client, _, key) = await host.PairedClientAsync();
            await using var _ = client;
            using var __ = key;
            var status = await client.RequestAsync("rgb.status");
            Assert.False(status["payload"]!["available"]!.GetValue<bool>());
            Assert.Contains("OpenRGB", status["payload"]!["reason"]!.GetValue<string>(), StringComparison.Ordinal);
            var apply = await client.RequestAsync("rgb.apply", new System.Text.Json.Nodes.JsonObject { ["color"] = "#FFFFFF", ["brightness"] = 100, ["effect"] = "static" });
            Assert.Equal(Nexus.Core.Protocol.ErrorCodes.RgbUnavailable, apply.ErrorCode());
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }
}

public sealed class OpenRgbHostTests
{
    [Fact]
    public void Bundled_server_listens_on_loopback_only_with_its_own_settings_folder()
    {
        var args = Nexus.Agent.Server.OpenRgbHost.ServerArguments(@"C:\ProgramData\NexusPC\OpenRGB", 6742);
        Assert.Equal(["--server", "--server-host", "127.0.0.1", "--server-port", "6742", "--noautoconnect", "--config", @"C:\ProgramData\NexusPC\OpenRGB"], args);
        Assert.Equal("--gui", Nexus.Agent.Server.OpenRgbHost.GuiArguments("c", 6742)[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_copy_is_known_before_the_host_starts(bool withPath)
    {
        string? executable = withPath ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "OpenRGB.exe") : null;
        var host = new Nexus.Agent.Server.OpenRgbHost(executable, Path.GetTempPath(), 6742, Microsoft.Extensions.Logging.Abstractions.NullLogger<Nexus.Agent.Server.OpenRgbHost>.Instance);
        Assert.Equal("notInstalled", host.State);
    }

    [Fact]
    public void Present_copy_reads_starting_until_the_host_runs()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var exe = Path.Combine(dir, "OpenRGB.exe");
            File.WriteAllBytes(exe, []);
            var host = new Nexus.Agent.Server.OpenRgbHost(exe, dir, 6742, Microsoft.Extensions.Logging.Abstractions.NullLogger<Nexus.Agent.Server.OpenRgbHost>.Instance);
            Assert.Equal(OperatingSystem.IsWindows() ? "starting" : "notInstalled", host.State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Pausing_needs_the_bundled_copy()
    {
        await using var host = await AgentTestHost.StartAsync();
        var reply = await host.Admin.HandleAsync(new System.Text.Json.Nodes.JsonObject { ["op"] = "openrgb.pause" }, new Nexus.Agent.Admin.AdminConnectionState(), CancellationToken.None);
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Equal("notInstalled", host.Services.OpenRgb!.State);
    }
}
