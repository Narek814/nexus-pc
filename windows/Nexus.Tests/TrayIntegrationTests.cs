using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Protocol;
using Nexus.Core.Session;

namespace Nexus.Tests;

/// <summary>Phone (DevClient) → agent (TLS WebSocket) → session pipe → Tray handler (fake desktop).</summary>
public sealed class TrayIntegrationTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;
    private FakeTray _tray = null!;

    public async Task InitializeAsync()
    {
        _host = await AgentTestHost.StartAsync();
        _tray = await _host.ConnectTrayAsync();
        _tray.Store.AddApp("Steam", AppLaunchKind.Executable, @"C:\Program Files (x86)\Steam\steam.exe", true, @"C:\Program Files (x86)\Steam\steam.exe");
        _tray.Store.AddApp("Discord", AppLaunchKind.Shortcut, @"C:\Users\me\Discord.lnk", false, null);
    }

    public async Task DisposeAsync()
    {
        await _tray.DisposeAsync();
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task Phone_sees_desktop_capabilities_and_app_names_but_no_paths()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var hello = await client.RequestAsync("system.hello");
        Assert.Contains("apps.launch", hello["payload"]!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("available", (await client.RequestAsync("pc.status"))["payload"]!["userSession"]!.GetValue<string>());

        var apps = await client.RequestAsync("apps.list");
        Assert.True(apps.IsOk(), apps.ToJsonString());
        Assert.Equal(["discord", "steam"], apps["payload"]!["apps"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
        Assert.DoesNotContain("Program Files", apps.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Phone_launches_apps_and_runs_profiles_in_the_user_session()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        Assert.True((await client.RequestAsync("apps.launch", new JsonObject { ["appId"] = "steam" })).IsOk());
        Assert.Equal(ErrorCodes.AppNotFound, (await client.RequestAsync("apps.launch", new JsonObject { ["appId"] = "powershell" })).ErrorCode());

        var upsert = await client.RequestAsync("profiles.upsert", new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = "gaming", ["name"] = "Gaming", ["appIds"] = new JsonArray("steam", "discord") },
        });
        Assert.True(upsert.IsOk(), upsert.ToJsonString());
        var run = await client.RequestAsync("profiles.run", new JsonObject { ["profileId"] = "gaming" });
        Assert.Equal(2, run["payload"]!["started"]!.GetValue<int>());

        Assert.Equal(["launch:steam", "launch:steam", "launch:discord"], _tray.Desktop.Calls);
        Assert.Equal(["gaming"], (await client.RequestAsync("profiles.list"))["payload"]!["profiles"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Phone_cannot_put_unknown_apps_into_a_profile()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var response = await client.RequestAsync("profiles.upsert", new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = "evil", ["name"] = "Evil", ["appIds"] = new JsonArray("cmd") },
        });
        Assert.Equal(ErrorCodes.ProfileInvalid, response.ErrorCode());
        Assert.Empty(_tray.Store.Profiles);
    }

    [Fact]
    public async Task Apps_permission_is_required_even_with_a_tray()
    {
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        _host.Services.Devices.SetPermissions(deviceId, Nexus.Core.Security.Permission.Monitoring);

        Assert.Equal(ErrorCodes.PermissionDenied, (await client.RequestAsync("apps.launch", new JsonObject { ["appId"] = "steam" })).ErrorCode());
        Assert.Empty(_tray.Desktop.Calls);
    }

    [Fact]
    public async Task Lock_goes_to_the_tray()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        Assert.True((await client.RequestAsync("power.lock")).IsOk());
        Assert.Equal(["lock"], _tray.Desktop.Calls);
    }

    [Fact]
    public async Task Non_forced_shutdown_runs_in_the_user_session_without_extra_delay()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var response = await client.RequestAsync("power.shutdown", new JsonObject { ["delaySeconds"] = 0 }, confirmed: true);
        Assert.True(response.IsOk(), response.ToJsonString());
        Assert.Equal("userSession", response["payload"]!["via"]!.GetValue<string>());
        Assert.Equal(["power:Shutdown:0:False"], _tray.Desktop.Calls);
        Assert.Empty(_host.Power!.Calls); // the service did not force anything
    }

    [Fact]
    public async Task Forced_shutdown_still_uses_the_service()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var response = await client.RequestAsync("power.restart", new JsonObject { ["delaySeconds"] = 60, ["force"] = true }, confirmed: true);
        Assert.Equal("service", response["payload"]!["via"]!.GetValue<string>());
        Assert.Empty(_tray.Desktop.Calls);
        Assert.StartsWith("Restart:60:True:", Assert.Single(_host.Power!.Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_tray_goes_away_power_falls_back_to_the_service_with_a_countdown()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        await _tray.DisposeAsync();
        await AgentIntegrationTests.WaitUntil(() => !_host.Services.Trays.IsAvailable);
        _tray = await _host.ConnectTrayAsync(); // keep DisposeAsync happy
        await _tray.DisposeAsync();
        await AgentIntegrationTests.WaitUntil(() => !_host.Services.Trays.IsAvailable);

        var response = await client.RequestAsync("power.shutdown", confirmed: true);
        Assert.Equal("service", response["payload"]!["via"]!.GetValue<string>());
        Assert.Equal(Nexus.Agent.Server.CommandDispatcher.MinGraceSeconds, response["payload"]!["delaySeconds"]!.GetValue<int>());
        Assert.Equal(ErrorCodes.SessionUnavailable, (await client.RequestAsync("apps.list")).ErrorCode());

        _tray = await _host.ConnectTrayAsync();
    }

    [Fact]
    public async Task Tray_can_open_pairing_and_approve_a_phone()
    {
        var started = await _tray.CallAgentAsync(SessionOps.PairStart);
        Assert.True(started.Ok);
        Assert.True(Nexus.Core.Pairing.PairingLink.TryParse(started.Result!["link"]!.GetValue<string>(), out var link));

        await using var phone = await _host.ConnectAsync(link.CertificateFingerprint);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var submit = phone.PairAsync(link.Code, "Narek's iPhone", key);

        var pending = await _tray.CallAgentAsync(SessionOps.PairWait);
        Assert.Equal("Narek's iPhone", pending.Result!["deviceName"]!.GetValue<string>());
        Assert.True((await _tray.CallAgentAsync(SessionOps.PairDecide, new JsonObject { ["approve"] = true })).Ok);

        Assert.True((await submit.WaitAsync(TimeSpan.FromSeconds(10))).IsOk());
        Assert.Single(_host.Services.Devices.All);
    }

    [Fact]
    public async Task Tray_cannot_grant_permissions()
    {
        var result = await _tray.CallAgentAsync("devices.permissions", new JsonObject { ["deviceId"] = Guid.NewGuid().ToString("D"), ["permissions"] = new JsonArray("screen") });
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Tray_session_pipe_refuses_login_start_and_unlink_but_answers_login_status()
    {
        // E4-6 (S2): whoever the tray user is, sign-in and unlink need the admin pipe.
        foreach (var op in new[] { SessionOps.LoginStart, SessionOps.Unlink })
        {
            var refused = await _tray.CallAgentAsync(op);
            Assert.False(refused.Ok);
            Assert.Equal(SessionOps.AdminRequired, refused.ErrorCode);
        }
        // login.status still runs for everyone (here: no account server configured, so "disabled").
        var status = await _tray.CallAgentAsync(SessionOps.LoginStatus);
        Assert.NotEqual(SessionOps.AdminRequired, status.ErrorCode);
        Assert.NotEqual(ErrorCodes.PermissionDenied, status.ErrorCode);
    }

    [Fact]
    public async Task Closing_the_tray_cancels_pairing_it_opened()
    {
        var started = await _tray.CallAgentAsync(SessionOps.PairStart);
        Assert.True(Nexus.Core.Pairing.PairingLink.TryParse(started.Result!["link"]!.GetValue<string>(), out var link));
        await _tray.DisposeAsync();
        await AgentIntegrationTests.WaitUntil(() => !_host.Services.Trays.IsAvailable);

        await using var phone = await _host.ConnectAsync();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.Equal(ErrorCodes.PairNoActiveSession, (await phone.PairAsync(link.Code, "x", key)).ErrorCode());

        _tray = await _host.ConnectTrayAsync();
    }
}

public sealed class TrayAuthenticityTests
{
    [Fact]
    public async Task On_windows_a_process_that_is_not_the_installed_tray_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the executable check uses Windows APIs; covered by the windows-latest CI job
        }
        await using var host = await AgentTestHost.StartAsync(trayPath: @"C:\Program Files\NexusPC\Tray\Nexus.Tray.exe");
        try
        {
            await using var impostor = await host.ConnectTrayUncheckedAsync();
            await Task.Delay(1000);
            Assert.False(host.Services.Trays.IsAvailable);
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }
}
