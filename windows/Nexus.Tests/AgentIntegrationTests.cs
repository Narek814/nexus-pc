using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Nexus.Agent.Admin;
using Nexus.Core.Protocol;

namespace Nexus.Tests;

/// <summary>End-to-end over real TLS WebSockets: Nexus.DevClient ↔ Nexus.Agent.</summary>
public sealed class AgentIntegrationTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task Pair_authenticate_and_schedule_a_shutdown()
    {
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var status = await client.RequestAsync("pc.status");
        Assert.True(status.IsOk(), status.ToJsonString());
        Assert.Equal(_host.PcId.ToString("D"), status["payload"]!["pcId"]!.GetValue<string>());
        Assert.Equal("unavailable", status["payload"]!["userSession"]!.GetValue<string>());

        var shutdown = await client.RequestAsync("power.shutdown", new JsonObject { ["delaySeconds"] = 600 }, confirmed: true);
        Assert.True(shutdown.IsOk(), shutdown.ToJsonString());
        Assert.True(_host.Power!.Calls.TryDequeue(out var call));
        Assert.Equal("Shutdown:600:False:Nexus PC: shutdown requested from Test iPhone.", call);

        Assert.True((await client.RequestAsync("power.cancel")).IsOk());
        Assert.True(_host.Power.Calls.TryDequeue(out var cancel));
        Assert.Equal("Cancel", cancel);

        var audit = await File.ReadAllLinesAsync(_host.Services.Audit.FilePath);
        Assert.Contains(audit, l => l.Contains("\"type\":\"power.shutdown\"", StringComparison.Ordinal) && l.Contains(deviceId.ToString("D"), StringComparison.Ordinal) && l.Contains("\"ok\":true", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, l => l.Contains("delaySeconds", StringComparison.Ordinal)); // no payloads in the log
    }

    [Fact]
    public async Task Shutdown_without_confirmation_is_refused_and_nothing_happens()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        Assert.Equal(ErrorCodes.ConfirmationRequired, (await client.RequestAsync("power.shutdown", new JsonObject { ["delaySeconds"] = 60 })).ErrorCode());
        Assert.Empty(_host.Power!.Calls);
    }

    [Fact]
    public async Task Immediate_actions_answer_first_then_act()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var sleep = await client.RequestAsync("power.sleep");
        Assert.True(sleep.IsOk());
        await WaitUntil(() => _host.Power!.Calls.Contains("Sleep"));
    }

    [Fact]
    public async Task Unauthenticated_connection_can_only_say_hello()
    {
        await using var client = await _host.ConnectAsync();

        var hello = await client.RequestAsync("system.hello");
        Assert.True(hello.IsOk());
        Assert.False(hello["payload"]!["authenticated"]!.GetValue<bool>());
        Assert.Contains("power.shutdown", hello["payload"]!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain("apps.launch", hello["payload"]!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.Equal(ErrorCodes.AuthRequired, (await client.RequestAsync("power.shutdown", confirmed: true)).ErrorCode());
        Assert.Equal(ErrorCodes.AuthRequired, (await client.RequestAsync("pc.status")).ErrorCode());
        Assert.Empty(_host.Power!.Calls);
    }

    [Fact]
    public async Task Wrong_certificate_pin_refuses_to_connect()
    {
        var wrong = new string('0', 64);
        await Assert.ThrowsAnyAsync<WebSocketException>(() => _host.ConnectAsync(wrong));
    }

    [Fact]
    public async Task Signature_from_another_key_fails_and_three_failures_close_the_connection()
    {
        await using var first = await _host.ConnectAsync();
        var (deviceId, realKey, _) = await _host.PairAsync(first);
        realKey.Dispose();

        await using var attacker = await _host.ConnectAsync();
        using var wrongKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        for (var i = 0; i < 3; i++)
        {
            var result = await attacker.AuthenticateAsync(_host.PcId, deviceId, wrongKey, _host.Fingerprint);
            Assert.Equal(ErrorCodes.AuthFailed, result.ErrorCode());
        }
        await Assert.ThrowsAnyAsync<WebSocketException>(() => attacker.RequestAsync("system.hello"));
    }

    [Fact]
    public async Task A_nonce_allows_exactly_one_proof_attempt()
    {
        await using var client = await _host.ConnectAsync();
        var (deviceId, key, _) = await _host.PairAsync(client);
        using var __ = key;

        var begin = await client.RequestAsync("auth.begin", new JsonObject { ["deviceId"] = deviceId.ToString("D") });
        var nonce = Convert.FromBase64String(begin["payload"]!["nonce"]!.GetValue<string>());
        var good = key.SignData(Nexus.Core.Security.DeviceCrypto.AuthTranscript(_host.PcId, deviceId, nonce, _host.Fingerprint),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);

        var wrong = await client.RequestAsync("auth.prove", new JsonObject { ["signature"] = Convert.ToBase64String(new byte[70]) });
        Assert.Equal(ErrorCodes.AuthFailed, wrong.ErrorCode());
        // The correct signature for the already-used nonce must not work any more.
        var late = await client.RequestAsync("auth.prove", new JsonObject { ["signature"] = Convert.ToBase64String(good) });
        Assert.Equal(ErrorCodes.AuthFailed, late.ErrorCode());
    }

    [Fact]
    public async Task Unknown_device_gets_the_same_challenge_but_cannot_authenticate()
    {
        await using var client = await _host.ConnectAsync();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var result = await client.AuthenticateAsync(_host.PcId, Guid.NewGuid(), key, _host.Fingerprint);
        Assert.Equal(ErrorCodes.AuthFailed, result.ErrorCode());
    }

    [Fact]
    public async Task Revoking_a_device_takes_effect_on_its_open_connection()
    {
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        Assert.True((await client.RequestAsync("pc.status")).IsOk());

        var revoked = await _host.Admin.HandleAsync(new JsonObject { ["op"] = "devices.revoke", ["deviceId"] = deviceId.ToString("D") }, new AdminConnectionState(), CancellationToken.None);
        Assert.True(revoked.IsOk());

        Assert.Equal(ErrorCodes.AuthRequired, (await client.RequestAsync("pc.status")).ErrorCode());
    }

    [Fact]
    public async Task Permission_changes_apply_immediately()
    {
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        await _host.Admin.HandleAsync(new JsonObject
        {
            ["op"] = "devices.permissions",
            ["deviceId"] = deviceId.ToString("D"),
            ["permissions"] = new JsonArray("monitoring"),
        }, new AdminConnectionState(), CancellationToken.None);

        Assert.Equal(ErrorCodes.PermissionDenied, (await client.RequestAsync("power.sleep")).ErrorCode());
        Assert.True((await client.RequestAsync("pc.status")).IsOk());
    }

    [Fact]
    public async Task Replayed_message_is_rejected_over_the_wire()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        var id = Guid.NewGuid();

        Assert.True((await client.RequestAsync("power.cancel", id: id)).IsOk());
        Assert.Equal(ErrorCodes.Replay, (await client.RequestAsync("power.cancel", id: id)).ErrorCode());
        Assert.Single(_host.Power!.Calls);
    }

    [Fact]
    public async Task Not_yet_implemented_commands_say_so()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var launch = await client.RequestAsync("apps.launch", new JsonObject { ["appId"] = "steam" });
        Assert.Equal(ErrorCodes.NotImplemented, launch.ErrorCode());
        Assert.Contains("P1c", launch["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_input_gets_an_error_with_null_id()
    {
        await using var client = await _host.ConnectAsync();
        var response = await client.SendRawAsync("{not json");
        Assert.Equal(ErrorCodes.Malformed, response.ErrorCode());
        Assert.Null(response["id"]);
    }

    [Fact]
    public async Task Oversized_message_closes_the_connection()
    {
        await using var client = await _host.ConnectAsync();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => client.SendRawAsync(new string('x', ProtocolInfo.MaxMessageBytes + 1)));
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, client.CloseStatus);
    }

    [Fact]
    public async Task Browser_origin_is_refused()
    {
        await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            _host.ConnectAsync(configure: o => o.SetRequestHeader("Origin", "https://evil.example")));
    }

    [Fact]
    public async Task Rejected_pairing_stores_nothing()
    {
        await using var client = await _host.ConnectAsync();
        var (_, key, response) = await _host.PairAsync(client, approve: false);
        key.Dispose();
        Assert.Equal(ErrorCodes.PairRejected, response.ErrorCode());
        Assert.Empty(_host.Services.Devices.All);
    }

    [Fact]
    public async Task Pairing_without_an_open_session_is_refused()
    {
        await using var client = await _host.ConnectAsync();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.Equal(ErrorCodes.PairNoActiveSession, (await client.PairAsync("12345678", "x", key)).ErrorCode());
    }

    [Fact]
    public async Task Closing_the_admin_window_cancels_a_waiting_pairing()
    {
        await using var client = await _host.ConnectAsync();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var state = new AdminConnectionState();
        var started = await _host.Admin.HandleAsync(new JsonObject { ["op"] = "pair.start" }, state, CancellationToken.None);

        var submit = client.PairAsync(started["code"]!.GetValue<string>(), "x", key);
        await _host.Admin.HandleAsync(new JsonObject { ["op"] = "pair.wait" }, state, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        _host.Admin.ConnectionClosed(state);

        Assert.Equal(ErrorCodes.PairExpired, (await submit.WaitAsync(TimeSpan.FromSeconds(10))).ErrorCode());
        Assert.Empty(_host.Services.Devices.All);
    }

    [Fact]
    public async Task Pairing_link_points_at_this_agent()
    {
        var link = _host.Admin.CreateLink("01234567");
        Assert.True(Nexus.Core.Pairing.PairingLink.TryParse(link.ToString(), out var parsed));
        Assert.Equal((_host.PcId, _host.Port, _host.Fingerprint), (parsed.PcId, parsed.Port, parsed.CertificateFingerprint));
        Assert.Equal(["127.0.0.1"], parsed.Hosts);
    }

    [Fact]
    public async Task Real_responses_have_the_shape_of_response_schema()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        var responses = new[]
        {
            await client.RequestAsync("system.hello"),
            await client.RequestAsync("pc.status"),
            await client.RequestAsync("power.shutdown"),
            await client.RequestAsync("apps.launch", new JsonObject { ["appId"] = "steam" }),
            await client.SendRawAsync("[]"),
        };
        foreach (var r in responses)
        {
            var keys = r.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
            var ok = r["ok"]!.GetValue<bool>();
            Assert.Equal(ok ? ["id", "kind", "ok", "payload", "v"] : ["error", "id", "kind", "ok", "v"], keys);
            Assert.Equal(1, r["v"]!.GetValue<int>());
            Assert.Equal("response", r["kind"]!.GetValue<string>());
            if (!ok)
            {
                Assert.Matches(@"^[a-z]+\.[A-Za-z]+$|^internal$", r["error"]!["code"]!.GetValue<string>());
            }
        }
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }
}

public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task Identity_and_devices_survive_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Guid pcId, deviceId;
            string fingerprint;
            System.Security.Cryptography.ECDsa key;
            await using (var first = await AgentTestHost.StartAsync(dir))
            {
                var (client, id, k) = await first.PairedClientAsync();
                await client.DisposeAsync();
                (pcId, deviceId, fingerprint, key) = (first.PcId, id, first.Fingerprint, k);
            }

            await using var second = await AgentTestHost.StartAsync(dir);
            Assert.Equal((pcId, fingerprint), (second.PcId, second.Fingerprint));
            await using var again = await second.ConnectAsync();
            Assert.True((await again.AuthenticateAsync(pcId, deviceId, key, fingerprint)).IsOk());
            key.Dispose();
        }
        finally
        {
            AgentTestHost.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Data_directory_is_private_to_the_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows ACLs are checked by the manual test plan (docs/TESTING.md).
        }
        await using var host = await AgentTestHost.StartAsync();
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(host.DataDirectory));
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }

    [Fact]
    public async Task Connection_that_never_authenticates_is_closed()
    {
        await using var host = await AgentTestHost.StartAsync(handshakeTimeout: TimeSpan.FromMilliseconds(500));
        try
        {
            await using var client = await host.ConnectAsync();
            await Task.Delay(1500);
            await Assert.ThrowsAnyAsync<WebSocketException>(() => client.RequestAsync("system.hello"));
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }

    [Fact]
    public async Task Power_on_a_non_windows_host_is_reported_as_unsupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        await using var host = await AgentTestHost.StartAsync(fakePower: false);
        try
        {
            var (client, _, key) = await host.PairedClientAsync();
            await using var _ = client;
            using var __ = key;
            Assert.Equal(ErrorCodes.PowerUnsupported, (await client.RequestAsync("power.sleep")).ErrorCode());
            Assert.Equal(ErrorCodes.PowerUnsupported, (await client.RequestAsync("power.shutdown", confirmed: true)).ErrorCode());
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }

    [Fact]
    public async Task Admin_pipe_serves_requests()
    {
        var pipe = "NexusPC.Test." + Guid.NewGuid().ToString("N");
        await using var host = await AgentTestHost.StartAsync(adminPipe: pipe);
        try
        {
            await using var admin = await AdminClient.ConnectAsync(pipe, CancellationToken.None);
            var status = await admin.SendAsync(new JsonObject { ["op"] = "status" }, CancellationToken.None);
            Assert.True(status.IsOk(), status.ToJsonString());
            Assert.Equal(host.Fingerprint, status["fingerprint"]!.GetValue<string>());
            Assert.False((await admin.SendAsync(new JsonObject { ["op"] = "rm -rf" }, CancellationToken.None)).IsOk());
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }
}
