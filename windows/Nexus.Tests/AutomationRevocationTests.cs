using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Agent.Server;
using Nexus.Agent.Storage;
using Nexus.Core.Automation;
using Nexus.Core.Protocol;
using Nexus.Core.Security;
using Xunit;

namespace Nexus.Tests;

/// <summary>
/// SA-T3-3: revoking a device while its automations run (T1–T8) and SA-T3-1: the global limit (L1–L3).
/// Races are driven by events (FakeDesktop.HoldLock keeps a lock step inside the revocation gate), not by timing.
/// </summary>
public sealed class AutomationRevocationTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;
    private readonly List<IAsyncDisposable> _clients = [];
    private readonly List<ECDsa> _keys = [];

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync(metrics: new FakeMetricsSource());

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }
        foreach (var key in _keys)
        {
            key.Dispose();
        }
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    private AutomationService Automations => _host.Services.Automations!;

    private async Task<(Guid Id, ECDsa Key)> DeviceAsync(Permission permissions = Permissions.DefaultAfterPairing | Permission.Automation)
    {
        var (client, id, key) = await _host.PairedClientAsync();
        _clients.Add(client);
        _keys.Add(key);
        _host.Services.Devices.SetPermissions(id, permissions);
        return (id, key);
    }

    private AutomationDefinition Save(Guid owner, string json)
    {
        Assert.True(AutomationJson.Parse(JsonElement.Parse(json), owner.ToString("D"), out var rule, out var error), error);
        Assert.Null(Automations.Upsert(rule!, owner.ToString("D")));
        return Automations.Find(rule!.Id)!;
    }

    private static string Rule(string id, string actions, string trigger = """{"type":"pcStarted"}""") =>
        $$"""{"id":"{{id}}","name":"{{id}}","trigger":{{trigger}},"actions":{{actions}}}""";

    private Task<JsonObject> AdminAsync(JsonObject request) =>
        _host.Admin.HandleAsync(request, new Nexus.Agent.Admin.AdminConnectionState(), CancellationToken.None);

    private Task<JsonObject> RevokeAsync(Guid device) =>
        AdminAsync(new JsonObject { ["op"] = "devices.revoke", ["deviceId"] = device.ToString("D") });

    private List<HistoryEntry> History() => _host.Services.History.List(200, null).ToList();

    private bool Finished(string name, string outcome) => History().Any(e => e.Title == $"Автоматизация «{name}»: {outcome}");

    private static int Count(FakeTray tray, string call) => tray.Desktop.Calls.Count(c => c == call);

    [Fact]
    public async Task T1_revocation_waits_for_the_active_step_and_no_waiting_step_of_the_device_runs()
    {
        await using var tray = await _host.ConnectTrayAsync();
        using var hold = new ManualResetEventSlim();
        tray.Desktop.HoldLock = hold;
        var (b, _) = await DeviceAsync();
        var active = Save(b, Rule("b1", """[{"type":"lock"},{"type":"sleep"}]"""));
        var waiting = Save(b, Rule("b2", """[{"type":"sleep"}]"""));

        Assert.True(Automations.Start(active, "test"));
        Assert.True(tray.Desktop.LockEntered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("b1", Automations.InFlightRuleId);
        Assert.True(Automations.Start(waiting, "test")); // waits for the gate held by b1's lock step

        var revoke = RevokeAsync(b);
        await AgentIntegrationTests.WaitUntil(() => Automations.IsRevoking(b));
        Assert.False(revoke.IsCompleted); // the gate is still held by the step that already started
        Assert.Equal("b1", Automations.InFlightRuleId); // the active step is not cancelled
        hold.Set();
        Assert.True((await revoke.WaitAsync(TimeSpan.FromSeconds(10))).IsOk());

        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Sleep", _host.Power!.Calls); // neither the waiting rule nor b1's next step
        Assert.Equal(1, Count(tray, "lock"));
        Assert.True(Finished("b2", "остановлена"));
        Assert.False(Finished("b1", "выполнена"));
        Assert.Null(Automations.InFlightRuleId);
        Assert.False(Automations.IsRevoking(b));
    }

    [Fact]
    public async Task T2_other_devices_rules_run_after_the_gate_is_released_and_the_revoked_one_does_not()
    {
        await using var tray = await _host.ConnectTrayAsync();
        using var hold = new ManualResetEventSlim();
        tray.Desktop.HoldLock = hold;
        var (a, _) = await DeviceAsync();
        var (b, _) = await DeviceAsync();
        var (c, _) = await DeviceAsync();
        var ruleA = Save(a, Rule("rule-a", """[{"type":"lock"}]"""));
        var ruleB = Save(b, Rule("rule-b", """[{"type":"sleep"}]"""));
        var ruleC = Save(c, Rule("rule-c", """[{"type":"lock"}]"""));

        Assert.True(Automations.Start(ruleA, "test"));
        Assert.True(tray.Desktop.LockEntered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(Automations.Start(ruleB, "test"));
        Assert.True(Automations.Start(ruleC, "test"));

        var revoke = RevokeAsync(b);
        await AgentIntegrationTests.WaitUntil(() => Automations.IsRevoking(b));
        Assert.False(revoke.IsCompleted);
        hold.Set();
        Assert.True((await revoke.WaitAsync(TimeSpan.FromSeconds(10))).IsOk());

        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Finished("rule-a", "выполнена"));
        Assert.True(Finished("rule-c", "выполнена"));
        Assert.False(Finished("rule-b", "выполнена"));
        Assert.Equal(2, Count(tray, "lock"));
        Assert.DoesNotContain("Sleep", _host.Power!.Calls);
    }

    [Fact]
    public async Task T3_revocation_cancels_a_long_wait_within_five_seconds_and_the_next_step_does_not_run()
    {
        var (b, _) = await DeviceAsync();
        var rule = Save(b, Rule("slow", """[{"type":"wait","seconds":3600},{"type":"sleep"}]"""));
        Assert.True(Automations.Start(rule, "test"));

        var watch = Stopwatch.StartNew();
        Assert.True((await RevokeAsync(b).WaitAsync(TimeSpan.FromSeconds(5))).IsOk());
        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.DoesNotContain("Sleep", _host.Power!.Calls);
        Assert.True(Finished("slow", "остановлена"));
    }

    [Fact]
    public async Task T4_while_a_revocation_waits_for_the_gate_new_runs_of_the_device_are_refused()
    {
        await using var tray = await _host.ConnectTrayAsync();
        using var hold = new ManualResetEventSlim();
        tray.Desktop.HoldLock = hold;
        var (a, _) = await DeviceAsync();
        var (b, _) = await DeviceAsync();
        var holder = Save(a, Rule("holder", """[{"type":"lock"}]"""));
        var manual = Save(b, Rule("manual", """[{"type":"sleep"}]"""));

        Assert.True(Automations.Start(holder, "test"));
        Assert.True(tray.Desktop.LockEntered.Wait(TimeSpan.FromSeconds(10)));
        var revoke = RevokeAsync(b);
        await AgentIntegrationTests.WaitUntil(() => Automations.IsRevoking(b));

        Assert.False(Automations.Start(manual, "test"));
        // Saved now, so its sensor can only fire while the revocation waits (the fake GPU reports 60 °C).
        Save(b, Rule("hot", """[{"type":"sleep"}]""", """{"type":"metric","metric":"gpuTemperature","above":55}"""));
        _host.Services.Metrics.SampleNow();
        Automations.Tick();
        await AgentIntegrationTests.WaitUntil(() => History().Any(e =>
            e.Title == "Автоматизация «hot»: пропущена" && e.Detail is { } d && d.StartsWith("Пропущена: устройство отзывается", StringComparison.Ordinal)));
        Assert.False(revoke.IsCompleted);

        hold.Set();
        Assert.True((await revoke.WaitAsync(TimeSpan.FromSeconds(10))).IsOk());
        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Sleep", _host.Power!.Calls);
        Assert.True(Finished("holder", "выполнена"));
    }

    [Fact]
    public async Task T5_waiting_rules_of_another_device_keep_working_while_a_device_is_revoked()
    {
        await using var tray = await _host.ConnectTrayAsync();
        using var hold = new ManualResetEventSlim();
        tray.Desktop.HoldLock = hold;
        var (a, _) = await DeviceAsync();
        var (b, _) = await DeviceAsync();
        var holder = Save(b, Rule("holder", """[{"type":"lock"}]"""));
        var atGate = Save(a, Rule("at-gate", """[{"type":"sleep"}]"""));
        var inWait = Save(a, Rule("in-wait", """[{"type":"wait","seconds":1},{"type":"lock"}]"""));

        Assert.True(Automations.Start(holder, "test"));
        Assert.True(tray.Desktop.LockEntered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(Automations.Start(atGate, "test"));
        Assert.True(Automations.Start(inWait, "test"));

        var revoke = RevokeAsync(b);
        await AgentIntegrationTests.WaitUntil(() => Automations.IsRevoking(b));
        hold.Set();
        Assert.True((await revoke.WaitAsync(TimeSpan.FromSeconds(10))).IsOk());

        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Finished("at-gate", "выполнена"));
        Assert.True(Finished("in-wait", "выполнена"));
        Assert.True(Finished("holder", "выполнена")); // its only step had started before the revocation
        Assert.Contains("Sleep", _host.Power!.Calls);
    }

    [Fact]
    public async Task T6_revoking_an_unknown_device_cancels_nothing()
    {
        var (a, _) = await DeviceAsync();
        var rule = Save(a, Rule("long", """[{"type":"wait","seconds":3600},{"type":"sleep"}]"""));
        Assert.True(Automations.Start(rule, "test"));

        var unknown = Guid.NewGuid();
        var called = false;
        var outcome = await Automations.RevokeDeviceAsync(unknown, () => called = true, CancellationToken.None);
        Assert.False(outcome.Changed);
        Assert.False(outcome.HasPowerRules);
        Assert.False(called);
        Assert.False(Automations.IsRevoking(unknown));
        var admin = await RevokeAsync(unknown);
        Assert.False(admin.IsOk());
        Assert.Equal("device not found", admin["error"]!.GetValue<string>());

        Assert.True(Automations.ToJson(a.ToString("D"))["automations"]![0]!["running"]!.GetValue<bool>());
        Assert.False(Finished("long", "остановлена"));
        Assert.True(Automations.Stop("long", a.ToString("D")));
        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task T7_the_mark_is_removed_when_remove_returns_false_or_throws_and_runs_are_possible_again()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (b, _) = await DeviceAsync();
        var rule = Save(b, Rule("x", """[{"type":"lock"}]"""));

        // remove() == false
        var outcome = await Automations.RevokeDeviceAsync(b, () => false, CancellationToken.None);
        Assert.False(outcome.Changed);
        Assert.False(Automations.IsRevoking(b));
        Assert.True(Automations.Start(rule, "test"));
        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Count(tray, "lock"));

        // Two revocations of one device at once: the mark stays until both are done.
        var seen = new List<bool>();
        bool Remove()
        {
            seen.Add(Automations.IsRevoking(b));
            return false;
        }
        await Task.WhenAll(Automations.RevokeDeviceAsync(b, Remove, CancellationToken.None), Automations.RevokeDeviceAsync(b, Remove, CancellationToken.None));
        Assert.Equal(new[] { true, true }, seen);
        Assert.False(Automations.IsRevoking(b));

        // remove() throws while a step of the device is inside the gate.
        using var hold = new ManualResetEventSlim();
        tray.Desktop.HoldLock = hold;
        tray.Desktop.LockEntered.Reset();
        Assert.True(Automations.Start(rule, "test"));
        Assert.True(tray.Desktop.LockEntered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("x", Automations.InFlightRuleId);
        var failing = Automations.RevokeDeviceAsync(b, () => throw new InvalidOperationException("store failed"), CancellationToken.None);
        await AgentIntegrationTests.WaitUntil(() => Automations.IsRevoking(b));
        Assert.False(failing.IsCompleted);
        hold.Set();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("store failed", error.Message);

        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Count(tray, "lock"));
        Assert.Equal(2, History().Count(e => e.Title == "Автоматизация «x»: выполнена")); // the step finished normally
        Assert.Null(Automations.InFlightRuleId);
        Assert.False(Automations.IsRevoking(b));
        Assert.NotNull(_host.Services.Devices.Find(b));

        Assert.True(Automations.Start(rule, "test"));
        await Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, Count(tray, "lock"));
    }

    [Fact]
    public async Task T8_warning_is_given_for_a_device_with_power_rules_even_if_they_never_ran()
    {
        var (withPower, _) = await DeviceAsync();
        var (without, _) = await DeviceAsync();
        Save(withPower, Rule("bed", """[{"type":"shutdown","delaySeconds":600}]""", """{"type":"time","time":"03:00"}"""));
        Save(without, Rule("night", """[{"type":"lock"}]""", """{"type":"time","time":"03:00"}"""));

        var warned = await RevokeAsync(withPower);
        Assert.True(warned.IsOk(), warned.ToJsonString());
        Assert.Equal(AutomationService.PowerRulesWarning, warned["warning"]!.GetValue<string>());
        Assert.StartsWith("У этого устройства есть автоматизации с выключением или перезагрузкой. Если одна из них уже запланировала",
            AutomationService.PowerRulesWarning, StringComparison.Ordinal);
        Assert.Empty(_host.Power!.Calls); // nothing ran: the warning comes from the stored rule
        var entry = History().First(e => e.Title.StartsWith("Устройство отозвано", StringComparison.Ordinal));
        Assert.StartsWith("Если одна из них уже запланировала", entry.Detail, StringComparison.Ordinal);

        var plain = await RevokeAsync(without);
        Assert.True(plain.IsOk(), plain.ToJsonString());
        Assert.Null(plain["warning"]);
    }

    [Fact]
    public async Task T8_warning_reaches_the_tray_in_the_rpc_result()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (withPower, _) = await DeviceAsync();
        var (without, _) = await DeviceAsync();
        Save(withPower, Rule("reboot", """[{"type":"restart","delaySeconds":600}]""", """{"type":"time","time":"03:00"}"""));

        var warned = await tray.CallAgentAsync(Nexus.Core.Session.SessionOps.DevicesRevoke, new JsonObject { ["deviceId"] = withPower.ToString("D") });
        Assert.True(warned.Ok, warned.Message);
        Assert.Equal(AutomationService.PowerRulesWarning, warned.Result?["warning"]?.GetValue<string>());
        Assert.Null(_host.Services.Devices.Find(withPower));

        var plain = await tray.CallAgentAsync(Nexus.Core.Session.SessionOps.DevicesRevoke, new JsonObject { ["deviceId"] = without.ToString("D") });
        Assert.True(plain.Ok, plain.Message);
        Assert.Null(plain.Result?["warning"]);
        Assert.Empty(_host.Power!.Calls);
    }

    private void FillToTheLimit(Guid owner)
    {
        for (var i = 0; i < AutomationRules.MaxAutomations; i++)
        {
            Save(owner, Rule($"r{i}", """[{"type":"lock"}]""", """{"type":"time","time":"04:00"}"""));
        }
    }

    [Fact]
    public async Task L1_at_the_limit_an_existing_id_can_still_be_updated()
    {
        var (a, _) = await DeviceAsync();
        FillToTheLimit(a);
        Assert.True(AutomationJson.Parse(JsonElement.Parse("""{"id":"r0","name":"Renamed","trigger":{"type":"time","time":"05:00"},"actions":[{"type":"sleep"}]}"""),
            a.ToString("D"), out var update, out _));
        Assert.Null(Automations.Upsert(update!, a.ToString("D")));
        Assert.Equal("Renamed", Automations.Find("r0")!.Name);
        Assert.Equal(AutomationRules.MaxAutomations, Automations.Rules.Count);
    }

    [Fact]
    public async Task L2_the_limit_is_global_across_devices()
    {
        var (a, _) = await DeviceAsync();
        var (b, _) = await DeviceAsync();
        FillToTheLimit(a);
        Assert.True(AutomationJson.Parse(JsonElement.Parse(Rule("other", """[{"type":"lock"}]""")), b.ToString("D"), out var rule, out _));
        var refused = Automations.Upsert(rule!, b.ToString("D"));
        Assert.Equal(ErrorCodes.AutomationLimit, refused?.Code);
        Assert.Null(Automations.Find("other"));
        Assert.Equal(AutomationRules.MaxAutomations, Automations.Rules.Count);
    }

    [Fact]
    public async Task L3_assign_is_refused_when_the_global_limit_is_reached()
    {
        var (gone, _) = await DeviceAsync();
        Save(gone, Rule("orphan", """[{"type":"lock"}]""", """{"type":"time","time":"04:00"}"""));
        Assert.True((await RevokeAsync(gone)).IsOk());
        var (a, aKey) = await DeviceAsync();
        FillToTheLimit(a);
        var stored = AutomationJson.ToJson(Automations.Find("orphan")!).ToJsonString();

        var error = await Automations.AssignOwnerAsync("orphan", a, DeviceCrypto.DeviceKeyFingerprint(aKey.ExportSubjectPublicKeyInfo()), CancellationToken.None);
        Assert.Equal($"limit of {AutomationRules.MaxAutomations} automations reached", error);
        Assert.Equal(stored, AutomationJson.ToJson(Automations.Find("orphan")!).ToJsonString());
        Assert.False(Automations.HasConfirmedOwner(Automations.Find("orphan")!));
    }
}
