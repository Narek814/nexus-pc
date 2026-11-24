using System.Text.Json.Nodes;
using Nexus.Core.Entitlements;
using Nexus.Core.Protocol;

namespace Nexus.Tests;

/// <summary>E7: plan checks on a real agent (TLS WebSocket), with the fixture's test keys injected through the host options.</summary>
public sealed class EntitlementIntegrationTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;
    private System.Text.Json.JsonDocument _doc = null!;
    private FakeEntitlementClock _clock = null!;

    public async Task InitializeAsync()
    {
        _doc = EntitlementVerifierTests.Fixture();
        _clock = new FakeEntitlementClock(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _host = await AgentTestHost.StartAsync(entitlementTrust: EntitlementVerifierTests.TestTrust(_doc), entitlementClock: _clock);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
        _doc.Dispose();
    }

    private string ProToken(long iat) =>
        EntitlementVerifierTests.Sign(_doc, EntitlementVerifierTests.Payload(_host.PcId, iat, iat + 86_400, iat + 86_400 + 604_800));

    private static JsonObject History(string range) => new() { ["range"] = range };

    [Fact]
    public async Task Hello_before_login_has_no_plan_fields_and_after_login_has_them()
    {
        await using (var anonymous = await _host.ConnectAsync())
        {
            var before = await anonymous.RequestAsync("system.hello");
            Assert.Null(before["payload"]!["plan"]);
            Assert.Null(before["payload"]!["features"]);
            Assert.Null(before["payload"]!["entitlementState"]);
            Assert.Contains("entitlements.v1", before["payload"]!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(ErrorCodes.AuthRequired, (await anonymous.RequestAsync("account.refresh")).ErrorCode());
        }
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        var hello = (await client.RequestAsync("system.hello"))["payload"]!;
        Assert.Equal("free", hello["plan"]!.GetValue<string>());
        Assert.Equal("none", hello["entitlementState"]!.GetValue<string>());
        Assert.Empty(hello["features"]!.AsArray());
        Assert.Null(hello["validUntil"]);
        Assert.Null(hello["sub"]);
        Assert.Null(hello["email"]);
        Assert.Null(hello["accountId"]);
    }

    [Fact]
    public async Task Pro_command_follows_the_token_through_active_grace_and_expiry()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var refused = await client.RequestAsync("metrics.history", History("1h"));
        Assert.Equal(ErrorCodes.PlanRequired, refused.ErrorCode());
        Assert.Equal(PlanPolicy.MonitoringExtended, refused["error"]!["details"]!["feature"]!.GetValue<string>());
        Assert.Equal("none", refused["error"]!["details"]!["state"]!.GetValue<string>());
        Assert.True((await client.RequestAsync("metrics.history", History("10m"))).IsOk()); // Free

        Assert.True(_host.Services.Entitlements.Accept(ProToken(_clock.Wall)).Ok);
        Assert.True((await client.RequestAsync("metrics.history", History("1h"))).IsOk());
        var hello = (await client.RequestAsync("system.hello"))["payload"]!;
        Assert.Equal("pro", hello["plan"]!.GetValue<string>());
        Assert.Equal("active", hello["entitlementState"]!.GetValue<string>());
        Assert.NotNull(hello["validUntil"]);

        _clock.Pass(86_400 + 60);
        Assert.True((await client.RequestAsync("metrics.history", History("24h"))).IsOk());
        Assert.Equal("grace", (await client.RequestAsync("system.hello"))["payload"]!["entitlementState"]!.GetValue<string>());

        _clock.Pass(604_800);
        var expired = await client.RequestAsync("metrics.history", History("1h"));
        Assert.Equal(ErrorCodes.PlanRequired, expired.ErrorCode());
        Assert.Equal("expired", expired["error"]!["details"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void Triggered_rules_need_pro_and_are_skipped_without_it()
    {
        var rule = new Nexus.Core.Automation.AutomationDefinition("r1", "Rule", true,
            new Nexus.Core.Automation.AutomationTrigger(Nexus.Core.Automation.TriggerKind.PcStarted), null,
            [new Nexus.Core.Automation.AutomationAction("rgbProfile", Id: "gaming")], null);
        Assert.Equal([PlanPolicy.Automations, PlanPolicy.RgbSync], PlanPolicy.RequiredFor(rule));
        var automations = _host.Services.Automations!;
        Assert.Equal(PlanPolicy.Automations, automations.MissingPlanFeature(rule));
        Assert.True(_host.Services.Entitlements.Accept(EntitlementVerifierTests.Sign(_doc, EntitlementVerifierTests.Payload(
            _host.PcId, _clock.Wall, _clock.Wall + 3600, _clock.Wall + 3600, features: "\"automations\""))).Ok);
        Assert.Equal(PlanPolicy.RgbSync, automations.MissingPlanFeature(rule));
        _clock.Pass(3601);
        Assert.Equal(PlanPolicy.Automations, automations.MissingPlanFeature(rule));    }

    [Fact]
    public async Task Manual_run_needs_the_plan_features_of_its_actions_but_upsert_still_saves()
    {
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        _host.Services.Devices.SetPermissions(deviceId, Nexus.Core.Security.Permissions.DefaultAfterPairing | Nexus.Core.Security.Permission.Automation);
        Assert.True(_host.Services.Entitlements.Accept(EntitlementVerifierTests.Sign(_doc, EntitlementVerifierTests.Payload(
            _host.PcId, _clock.Wall, _clock.Wall + 3600, _clock.Wall + 3600, features: "\"automations\""))).Ok);

        var upsert = new JsonObject
        {
            ["automation"] = JsonNode.Parse("""{"id":"play","name":"Play","trigger":{"type":"time","time":"20:00"},"actions":[{"type":"profile","profileId":"gaming"}]}"""),
        };
        var saved = await client.RequestAsync("automations.upsert", upsert);
        Assert.True(saved.IsOk(), saved.ToJsonString()); // saved; no warning field exists in the protocol (ENT-2b)
        Assert.Null(saved["payload"]!["warning"]);

        var refused = await client.RequestAsync("automations.run", new JsonObject { ["automationId"] = "play" });
        Assert.Equal(ErrorCodes.PlanRequired, refused.ErrorCode());
        Assert.Equal(PlanPolicy.Profiles, refused["error"]!["details"]!["feature"]!.GetValue<string>());
        Assert.Equal("active", refused["error"]!["details"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Device_without_permission_gets_permission_denied_not_plan_required()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        // automation is never granted at pairing; plan is missing too.
        var response = await client.RequestAsync("automations.delete", new JsonObject { ["automationId"] = "x" });
        Assert.Equal(ErrorCodes.PermissionDenied, response.ErrorCode());
    }

    [Fact]
    public async Task Free_commands_work_without_a_token()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        Assert.True((await client.RequestAsync("pc.status")).IsOk());
        Assert.True((await client.RequestAsync("metrics.snapshot")).IsOk());
        Assert.True((await client.RequestAsync("power.cancel")).IsOk());
        Assert.True((await client.RequestAsync("rgb.status")).IsOk());
    }

    [Fact]
    public async Task Account_refresh_answers_local_state_and_is_rate_limited_per_pc()
    {
        var (client, _, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        var first = await client.RequestAsync("account.refresh");
        Assert.True(first.IsOk(), first.ToJsonString());
        Assert.Equal("none", first["payload"]!["entitlementState"]!.GetValue<string>());
        var second = await client.RequestAsync("account.refresh");
        Assert.Equal(ErrorCodes.RateLimited, second.ErrorCode());
        Assert.InRange(second["error"]!["details"]!["retryAfter"]!.GetValue<int>(), 1, 60);
    }

    [Fact]
    public async Task Entitlement_token_is_not_accepted_in_auth()
    {
        await using var client = await _host.ConnectAsync();
        var token = ProToken(_clock.Wall);
        var begin = await client.RequestAsync("auth.begin", new JsonObject { ["deviceId"] = Guid.NewGuid().ToString("D") });
        Assert.True(begin.IsOk());
        var prove = await client.RequestAsync("auth.prove", new JsonObject { ["signature"] = token });
        Assert.False(prove.IsOk());
        Assert.False((await client.RequestAsync("system.hello"))["payload"]!["authenticated"]!.GetValue<bool>());
        Assert.Equal(EntitlementState.None, _host.Services.Entitlements.Current.State);
    }

    [Fact]
    public async Task State_survives_a_restart_and_the_file_is_private()
    {
        Assert.True(_host.Services.Entitlements.Accept(ProToken(_clock.Wall)).Ok);
        var dir = _host.DataDirectory;
        var path = Path.Combine(dir, Nexus.Agent.Storage.EntitlementFileStore.FileName);
        Assert.True(File.Exists(path));
        Assert.True(Nexus.Agent.Storage.EntitlementFileStore.IsTrusted(path));
        await _host.DisposeAsync();
        _host = await AgentTestHost.StartAsync(dataDirectory: dir, entitlementTrust: EntitlementVerifierTests.TestTrust(_doc), entitlementClock: _clock);
        Assert.Equal(EntitlementState.Active, _host.Services.Entitlements.Current.State);
    }

    [Fact]
    public async Task Untrusted_state_file_is_deleted_and_counts_as_no_token()
    {
        Assert.True(_host.Services.Entitlements.Accept(ProToken(_clock.Wall)).Ok);
        var dir = _host.DataDirectory;
        await _host.DisposeAsync();
        var path = Path.Combine(dir, Nexus.Agent.Storage.EntitlementFileStore.FileName);
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            var security = System.IO.FileSystemAclExtensions.GetAccessControl(info);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null), // trusted by the folder check, but not SYSTEM-only
                System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(info, security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        }
        _host = await AgentTestHost.StartAsync(dataDirectory: dir, entitlementTrust: EntitlementVerifierTests.TestTrust(_doc), entitlementClock: _clock);
        Assert.Equal(EntitlementState.None, _host.Services.Entitlements.Current.State);
        Assert.False(File.Exists(path) && !Nexus.Agent.Storage.EntitlementFileStore.IsTrusted(path));
    }
}
