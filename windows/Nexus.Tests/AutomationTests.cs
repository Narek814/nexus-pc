using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Automation;
using Nexus.Core.Protocol;
using Nexus.Core.Security;
using Xunit;

namespace Nexus.Tests;

public sealed class AutomationEngineTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    private static AutomationDefinition Rule(string json)
    {
        Assert.True(AutomationJson.Parse(JsonElement.Parse(json), "dev", out var rule, out var error), error);
        return rule!;
    }

    // 2026-11-16 is a Monday.
    private static DateTimeOffset Local(int day, int hour, int minute, int second = 0) =>
        new DateTimeOffset(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));

    [Fact]
    public void Time_rule_fires_once_in_its_minute_in_the_pc_time_zone()
    {
        var engine = new AutomationEngine(Moscow);
        var rule = Rule("""{"id":"night","name":"Night","trigger":{"type":"time","time":"23:00"},"actions":[{"type":"lock"}]}""");
        Assert.Empty(engine.Due([rule], Local(16, 22, 59, 59), null));
        Assert.Single(engine.Due([rule], Local(16, 23, 0, 1), null));
        Assert.Empty(engine.Due([rule], Local(16, 23, 0, 3), null));   // same day: not again
        Assert.Single(engine.Due([rule], Local(17, 23, 1, 0).ToUniversalTime(), null)); // next day, given in UTC
    }

    [Fact]
    public void A_pc_asleep_at_the_time_does_not_run_it_hours_later()
    {
        var engine = new AutomationEngine(Moscow);
        var rule = Rule("""{"id":"n","name":"N","trigger":{"type":"time","time":"23:00"},"actions":[{"type":"lock"}]}""");
        Assert.Empty(engine.Due([rule], Local(16, 23, 5), null));
    }

    [Fact]
    public void Days_limit_the_rule()
    {
        var engine = new AutomationEngine(Moscow);
        var weekdays = Rule("""{"id":"w","name":"W","trigger":{"type":"time","time":"08:00","days":[1,2,3,4,5]},"actions":[{"type":"lock"}]}""");
        Assert.Single(engine.Due([weekdays], Local(20, 8, 0), null)); // Friday
        Assert.Empty(engine.Due([weekdays], Local(21, 8, 0), null));  // Saturday
        Assert.Empty(engine.Due([weekdays], Local(22, 8, 0), null));  // Sunday
    }

    [Fact]
    public void Metric_rule_needs_the_hold_time_and_drops_before_repeating()
    {
        var engine = new AutomationEngine(Moscow);
        var rule = Rule("""{"id":"hot","name":"Hot","trigger":{"type":"metric","metric":"gpuTemperature","above":85,"forSeconds":30},"actions":[{"type":"lock"}]}""");
        var t = Local(16, 12, 0);
        AutomationReadings Gpu(double? c) => new(c, 10, 10);

        Assert.Empty(engine.Due([rule], t, Gpu(90)));
        Assert.Empty(engine.Due([rule], t.AddSeconds(29), Gpu(90)));
        Assert.Single(engine.Due([rule], t.AddSeconds(30), Gpu(90)));
        Assert.Empty(engine.Due([rule], t.AddSeconds(90), Gpu(90)));    // still hot: once
        Assert.Empty(engine.Due([rule], t.AddSeconds(100), Gpu(82)));   // not 5 below yet
        Assert.Empty(engine.Due([rule], t.AddSeconds(110), Gpu(null))); // sensor gone: no change
        Assert.Empty(engine.Due([rule], t.AddSeconds(120), Gpu(90)));
        Assert.Empty(engine.Due([rule], t.AddSeconds(200), Gpu(79)));   // re-armed
        Assert.Empty(engine.Due([rule], t.AddSeconds(210), Gpu(88)));
        Assert.Single(engine.Due([rule], t.AddSeconds(240), Gpu(88)));
    }

    [Fact]
    public void Cpu_temperature_can_trigger_a_rule()
    {
        var engine = new AutomationEngine(Moscow);
        var rule = Rule("""{"id":"cpu","name":"CPU","trigger":{"type":"metric","metric":"cpuTemperature","above":90,"forSeconds":60},"actions":[{"type":"lock"}]}""");
        var t = Local(16, 12, 0);
        Assert.Empty(engine.Due([rule], t, new AutomationReadings(40, 10, 10, CpuTemperature: 95)));
        Assert.Single(engine.Due([rule], t.AddSeconds(60), new AutomationReadings(40, 10, 10, CpuTemperature: 95)));
        Assert.Empty(new AutomationEngine(Moscow).Due([rule], t, new AutomationReadings(99, 10, 10, CpuTemperature: null)));
    }

    [Fact]
    public void Only_between_wraps_past_midnight()
    {
        var engine = new AutomationEngine(Moscow);
        var rule = Rule("""{"id":"s","name":"S","onlyBetween":{"from":"22:00","to":"07:00"},"trigger":{"type":"userSignedIn"},"actions":[{"type":"lock"}]}""");
        Assert.Single(engine.ForEvent([rule], TriggerKind.UserSignedIn, Local(16, 23, 30)));
        Assert.Single(engine.ForEvent([rule], TriggerKind.UserSignedIn, Local(17, 6, 59)));
        Assert.Empty(engine.ForEvent([rule], TriggerKind.UserSignedIn, Local(17, 7, 0)));
        Assert.Empty(engine.ForEvent([rule], TriggerKind.PcStarted, Local(16, 23, 30)));
    }

    [Fact]
    public void Disabled_rules_never_fire()
    {
        var engine = new AutomationEngine(Moscow);
        var rule = Rule("""{"id":"n","name":"N","enabled":false,"trigger":{"type":"time","time":"23:00"},"actions":[{"type":"lock"}]}""");
        Assert.Empty(engine.Due([rule], Local(16, 23, 0), null));
        Assert.Empty(engine.ForEvent([rule with { Trigger = new AutomationTrigger(TriggerKind.PcStarted) }], TriggerKind.PcStarted, Local(16, 23, 0)));
    }

    [Fact]
    public void Json_round_trips()
    {
        var rule = Rule("""
            {"id":"night","name":"Ночь","enabled":true,"onlyBetween":{"from":"22:00","to":"02:00"},
             "trigger":{"type":"time","time":"23:00","days":[5,6]},
             "actions":[{"type":"profile","profileId":"night"},{"type":"rgb","color":"#ff0000","brightness":40,"effect":"static"},
                        {"type":"wait","seconds":600},{"type":"shutdown","delaySeconds":300},{"type":"lock"}]}
            """);
        var again = Rule(AutomationJson.ToJson(rule).ToJsonString());
        Assert.Equal(AutomationJson.ToJson(rule).ToJsonString(), AutomationJson.ToJson(again).ToJsonString());
        Assert.Equal("#FF0000", again.Actions[1].Color);
    }

    [Fact]
    public void Required_permissions_follow_the_actions()
    {
        var rule = Rule("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"actions":[{"type":"app","appId":"steam"},{"type":"rgbProfile","profileId":"night"},{"type":"sleep"}]}""");
        Assert.Equal(Permission.Automation | Permission.Apps | Permission.Rgb | Permission.Power, AutomationRules.RequiredFor(rule));
        Assert.False(AutomationRules.NeedsConfirmation(rule));
    }

    [Theory]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"actions":[{"type":"wait","seconds":5}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted","time":"10:00"},"actions":[{"type":"lock"}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"time","time":"10:00","days":[0]},"actions":[{"type":"lock"}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"metric","metric":"diskFree","above":5},"actions":[{"type":"lock"}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"actions":[{"type":"notify","message":"removed"}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"actions":[{"type":"app","appId":"C:\\evil.exe"}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"onlyBetween":{"from":"10:00","to":"10:00"},"actions":[{"type":"lock"}]}""")]
    public void Rejects(string json) => Assert.False(AutomationJson.Parse(JsonElement.Parse(json), "dev", out _, out _));

    [Fact]
    public void Stored_notification_steps_are_dropped()
    {
        using var kept = AutomationJson.WithoutLegacySteps(JsonElement.Parse(
            """{"id":"hot","name":"Hot","trigger":{"type":"pcStarted"},"actions":[{"type":"notify","message":"hot"},{"type":"lock"}]}"""));
        Assert.True(AutomationJson.Parse(kept!.RootElement, null, out var rule, out _));
        Assert.Equal(["lock"], rule!.Actions.Select(a => a.Kind));
        Assert.Null(AutomationJson.WithoutLegacySteps(JsonElement.Parse(
            """{"id":"n","name":"N","trigger":{"type":"pcStarted"},"actions":[{"type":"notify","message":"only"}]}""")));
    }
}

public sealed class AutomationProtocolTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync(metrics: new FakeMetricsSource());

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    private static JsonObject Upsert(string json) => new() { ["automation"] = JsonNode.Parse(json) };

    private async Task<(Nexus.DevClient.NexusClient Client, Guid DeviceId, System.Security.Cryptography.ECDsa Key)> ClientWith(Permission permissions)
    {
        var paired = await _host.PairedClientAsync();
        _host.Services.Devices.SetPermissions(paired.DeviceId, permissions);
        return paired;
    }

    [Fact]
    public async Task A_rule_can_only_do_what_its_creator_may_do()
    {
        var (client, _, key) = await ClientWith(Permissions.DefaultAfterPairing);
        await using var _ = client;
        using var __ = key;
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.RequestAsync("automations.list")).ErrorCode());

        var (admin, adminId, adminKey) = await ClientWith(Permission.Automation | Permission.Monitoring);
        await using var ___ = admin;
        using var ____ = adminKey;
        var denied = await admin.RequestAsync("automations.upsert", Upsert("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"actions":[{"type":"app","appId":"steam"}]}"""));
        Assert.Equal(ErrorCodes.PermissionDenied, denied.ErrorCode());
        Assert.Contains("apps", denied["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        _host.Services.Devices.SetPermissions(adminId, Permission.Automation | Permission.Monitoring | Permission.Power);
        const string shutdown = """{"id":"bed","name":"Bed","trigger":{"type":"time","time":"01:30"},"actions":[{"type":"shutdown","delaySeconds":600}]}""";
        Assert.Equal(ErrorCodes.ConfirmationRequired, (await admin.RequestAsync("automations.upsert", Upsert(shutdown))).ErrorCode());
        var saved = await admin.RequestAsync("automations.upsert", Upsert(shutdown), confirmed: true);
        Assert.True(saved.IsOk(), saved.ToJsonString());
        Assert.Equal("bed", saved["payload"]!["automations"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Manual_run_executes_actions_and_writes_history()
    {
        await using var tray = await _host.ConnectTrayAsync();
        tray.Store.AddApp("Steam", Nexus.Core.Apps.AppLaunchKind.Executable, "steam.exe", false, null);
        tray.Store.UpsertProfile(new Nexus.Core.Apps.ProfileDefinition("gaming", "Gaming", ["steam"]));
        var (client, _, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;

        var saved = await client.RequestAsync("automations.upsert", Upsert("""
            {"id":"play","name":"Игра","trigger":{"type":"time","time":"20:00"},
             "actions":[{"type":"profile","profileId":"gaming"},{"type":"wait","seconds":1},{"type":"lock"}]}
            """));
        Assert.True(saved.IsOk(), saved.ToJsonString());
        Assert.True((await client.RequestAsync("automations.run", new JsonObject { ["automationId"] = "play" })).IsOk());
        Assert.Equal(ErrorCodes.AutomationBusy, (await client.RequestAsync("automations.run", new JsonObject { ["automationId"] = "play" })).ErrorCode());
        await _host.Services.Automations!.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("launch:steam", tray.Desktop.Calls);
        Assert.Contains("lock", tray.Desktop.Calls);

        var list = await client.RequestAsync("automations.list");
        Assert.True(list["payload"]!["automations"]![0]!["lastRun"]!["ok"]!.GetValue<bool>());

        var history = await client.RequestAsync("history.list", new JsonObject { ["limit"] = 20 });
        var titles = history["payload"]!["entries"]!.AsArray().Select(e => e!["title"]!.GetValue<string>()).ToList();
        Assert.Contains(titles, t => t.StartsWith("Автоматизация «Игра»", StringComparison.Ordinal));
        Assert.Contains("Автоматизация «play» запущена вручную", titles);
        Assert.Contains("Автоматизация сохранена", titles);
        Assert.DoesNotContain(titles, t => t.Contains("automations.list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_cancels_a_waiting_rule()
    {
        var (client, _, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;
        await client.RequestAsync("automations.upsert", Upsert("""{"id":"slow","name":"Slow","trigger":{"type":"pcStarted"},"actions":[{"type":"wait","seconds":3600},{"type":"sleep"}]}"""));
        await client.RequestAsync("automations.run", new JsonObject { ["automationId"] = "slow" });
        var stop = await client.RequestAsync("automations.stop", new JsonObject { ["automationId"] = "slow" });
        Assert.True(stop["payload"]!["stopped"]!.GetValue<bool>());
        await _host.Services.Automations!.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var summary = (await client.RequestAsync("automations.list"))["payload"]!["automations"]![0]!["lastRun"]!["summary"]!.GetValue<string>();
        Assert.StartsWith("остановлена", summary, StringComparison.Ordinal);
    }

    // Rewritten for owner-only automations (SEC-5). Before, a revoked creator's rule still started from its
    // trigger and failed at its first step. Now it is ownerless: the trigger skips it ("пропущена") and the rule
    // is kept for an administrator to reassign. A run started anyway still fails the fresh per-step check with
    // the same "отозвано" entry as before, so nothing that was checked before is checked less.
    [Fact]
    public async Task Revoking_the_creator_stops_its_rules_from_acting()
    {
        var (client, deviceId, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;
        Assert.True((await client.RequestAsync("automations.upsert", Upsert("""{"id":"lock","name":"Lock","trigger":{"type":"userSignedIn"},"actions":[{"type":"lock"}]}"""))).IsOk());

        Assert.True((await AdminAsync(new JsonObject { ["op"] = "devices.revoke", ["deviceId"] = deviceId.ToString("D") })).IsOk());
        await using var tray = await _host.ConnectTrayAsync(); // the sign-in trigger fires
        await AgentIntegrationTests.WaitUntil(() => _host.Services.History.List(20, null).Any(e => e.Title == "Автоматизация «Lock»: пропущена"));
        Assert.NotNull(_host.Services.Automations!.Find("lock")); // kept, not deleted

        Assert.True(_host.Services.Automations.Start(_host.Services.Automations.Find("lock")!, "test"));
        await _host.Services.Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("lock", tray.Desktop.Calls);
        var entry = _host.Services.History.List(5, null).First(e => e.Kind == "automation");
        Assert.False(entry.Ok);
        Assert.Contains("отозвано", entry.Detail, StringComparison.Ordinal);
    }

    private Task<JsonObject> AdminAsync(JsonObject request) =>
        _host.Admin.HandleAsync(request, new Nexus.Agent.Admin.AdminConnectionState(), CancellationToken.None);

    private static JsonObject Ref(string id) => new() { ["automationId"] = id };

    private async Task RestartWithRulesAsync(string rulesJson)
    {
        var dir = _host.DataDirectory;
        await _host.DisposeAsync();
        File.WriteAllText(Path.Combine(dir, "automations.json"), rulesJson);
        _host = await AgentTestHost.StartAsync(dataDirectory: dir, metrics: new FakeMetricsSource());
    }

    private int HistoryCount(string title) => _host.Services.History.List(200, null).Count(e => e.Title == title);

    [Fact]
    public async Task Another_device_cannot_list_run_stop_change_or_delete_a_rule()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (a, _, aKey) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = a;
        using var __ = aKey;
        var (b, _, bKey) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var ___ = b;
        using var ____ = bKey;

        Assert.True((await a.RequestAsync("automations.upsert", Upsert("""{"id":"mine","name":"Mine","trigger":{"type":"time","time":"03:00"},"actions":[{"type":"wait","seconds":3600},{"type":"lock"}]}"""))).IsOk());
        var before = AutomationJson.ToJson(_host.Services.Automations!.Find("mine")!).ToJsonString();

        Assert.Empty((await b.RequestAsync("automations.list"))["payload"]!["automations"]!.AsArray());
        Assert.Equal(ErrorCodes.AutomationNotFound, (await b.RequestAsync("automations.run", Ref("mine"))).ErrorCode());
        Assert.Equal(ErrorCodes.AutomationNotFound, (await b.RequestAsync("automations.upsert", Upsert("""{"id":"mine","name":"Taken","trigger":{"type":"pcStarted"},"actions":[{"type":"lock"}]}"""))).ErrorCode());
        Assert.Equal(ErrorCodes.AutomationNotFound, (await b.RequestAsync("automations.delete", Ref("mine"))).ErrorCode());

        // While it runs: B cannot stop it either, and it is still running afterwards.
        Assert.True((await a.RequestAsync("automations.run", Ref("mine"))).IsOk());
        Assert.Equal(ErrorCodes.AutomationNotFound, (await b.RequestAsync("automations.stop", Ref("mine"))).ErrorCode());
        Assert.True((await a.RequestAsync("automations.list"))["payload"]!["automations"]![0]!["running"]!.GetValue<bool>());
        Assert.Equal(before, AutomationJson.ToJson(_host.Services.Automations.Find("mine")!).ToJsonString());

        // The owner still can.
        Assert.True((await a.RequestAsync("automations.stop", Ref("mine")))["payload"]!["stopped"]!.GetValue<bool>());
        await _host.Services.Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await a.RequestAsync("automations.delete", Ref("mine"))).IsOk());
        Assert.Null(_host.Services.Automations.Find("mine"));
        Assert.DoesNotContain("lock", tray.Desktop.Calls);
    }

    [Fact]
    public async Task Manual_run_needs_the_callers_permissions_and_confirmation_for_shutdown()
    {
        var (client, deviceId, key) = await ClientWith(Permission.Automation | Permission.Monitoring | Permission.Power);
        await using var _ = client;
        using var __ = key;
        const string shutdown = """{"id":"bed","name":"Bed","trigger":{"type":"time","time":"01:30"},"actions":[{"type":"shutdown","delaySeconds":600}]}""";
        Assert.True((await client.RequestAsync("automations.upsert", Upsert(shutdown), confirmed: true)).IsOk());

        _host.Services.Devices.SetPermissions(deviceId, Permission.Automation | Permission.Monitoring);
        var denied = await client.RequestAsync("automations.run", Ref("bed"), confirmed: true);
        Assert.Equal(ErrorCodes.PermissionDenied, denied.ErrorCode());
        Assert.Contains("power", denied["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        _host.Services.Devices.SetPermissions(deviceId, Permission.Automation | Permission.Monitoring | Permission.Power);
        Assert.Equal(ErrorCodes.ConfirmationRequired, (await client.RequestAsync("automations.run", Ref("bed"))).ErrorCode());
        Assert.Empty(_host.Power!.Calls);

        Assert.True((await client.RequestAsync("automations.run", Ref("bed"), confirmed: true)).IsOk());
        await _host.Services.Automations!.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(_host.Power.Calls, c => c.StartsWith("Shutdown:600:False", StringComparison.Ordinal));
        Assert.Equal(ErrorCodes.AutomationNotFound, (await client.RequestAsync("automations.run", Ref("nothing"))).ErrorCode());
    }

    [Fact]
    public async Task Ownerless_rule_is_skipped_by_its_sensor_once_kept_and_not_counted()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (b, bId, bKey) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = b;
        using var __ = bKey;
        Assert.True((await b.RequestAsync("automations.upsert", Upsert("""{"id":"hot","name":"Hot","trigger":{"type":"metric","metric":"gpuTemperature","above":55},"actions":[{"type":"lock"}]}"""))).IsOk());
        Assert.True((await AdminAsync(new JsonObject { ["op"] = "devices.revoke", ["deviceId"] = bId.ToString("D") })).IsOk());

        for (var i = 0; i < 3; i++)
        {
            _host.Services.Metrics.SampleNow(); // the fake GPU stays at 60 °C
            _host.Services.Automations!.Tick();
        }
        await AgentIntegrationTests.WaitUntil(() => HistoryCount("Автоматизация «Hot»: пропущена") == 1);
        await Task.Delay(TimeSpan.FromSeconds(2.5)); // the service's own 2-second tick must not repeat it either
        _host.Services.Metrics.SampleNow();
        _host.Services.Automations!.Tick();
        Assert.Equal(1, HistoryCount("Автоматизация «Hot»: пропущена"));
        Assert.DoesNotContain("lock", tray.Desktop.Calls);
        Assert.NotNull(_host.Services.Automations.Find("hot"));

        // 32 places are still free for rules that have an owner; the 33rd is refused.
        var (a, _, aKey) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var ___ = a;
        using var ____ = aKey;
        for (var i = 0; i < AutomationRules.MaxAutomations; i++)
        {
            var saved = await a.RequestAsync("automations.upsert", Upsert($$"""{"id":"r{{i}}","name":"R{{i}}","trigger":{"type":"time","time":"04:00"},"actions":[{"type":"lock"}]}"""));
            Assert.True(saved.IsOk(), saved.ToJsonString());
        }
        Assert.Equal(ErrorCodes.AutomationLimit, (await a.RequestAsync("automations.upsert", Upsert("""{"id":"r99","name":"R99","trigger":{"type":"time","time":"04:00"},"actions":[{"type":"lock"}]}"""))).ErrorCode());
        Assert.Equal(AutomationRules.MaxAutomations + 1, _host.Services.Automations.Rules.Count);
    }

    [Fact]
    public async Task Ownerless_rule_is_skipped_by_its_schedule()
    {
        var now = DateTimeOffset.Now;
        if (now.Second >= 50)
        {
            await Task.Delay(TimeSpan.FromSeconds(61 - now.Second)); // stay inside one minute
            now = DateTimeOffset.Now;
        }
        var time = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        // One rule from an old version (no creator), one whose creator was never paired here.
        await RestartWithRulesAsync($$"""
            [{"id":"old","name":"Old","trigger":{"type":"time","time":"{{time}}"},"actions":[{"type":"sleep"}]},
             {"id":"gone","name":"Gone","trigger":{"type":"time","time":"{{time}}"},"actions":[{"type":"sleep"}],"createdBy":"{{Guid.NewGuid():D}}"}]
            """);
        Assert.Equal(2, _host.Services.Automations!.Rules.Count);
        _host.Services.Automations.Tick();
        await AgentIntegrationTests.WaitUntil(() => HistoryCount("Автоматизация «Old»: пропущена") == 1 && HistoryCount("Автоматизация «Gone»: пропущена") == 1);
        _host.Services.Automations.Tick();
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, HistoryCount("Автоматизация «Old»: пропущена"));
        Assert.Equal(1, HistoryCount("Автоматизация «Gone»: пропущена"));
        await _host.Services.Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("Sleep", _host.Power!.Calls);
        Assert.Equal(2, _host.Services.Automations.Rules.Count);
    }

    [Theory]
    [InlineData("devices.revoke")]
    [InlineData("devices.permissions")]
    public async Task A_change_while_a_step_runs_waits_for_it_and_stops_the_next_step(string op)
    {
        await using var tray = await _host.ConnectTrayAsync();
        using var hold = new ManualResetEventSlim();
        tray.Desktop.HoldLock = hold;
        var (client, deviceId, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;
        Assert.True((await client.RequestAsync("automations.upsert", Upsert("""{"id":"two","name":"Two","trigger":{"type":"pcStarted"},"actions":[{"type":"lock"},{"type":"sleep"}]}"""))).IsOk());
        Assert.True((await client.RequestAsync("automations.run", Ref("two"))).IsOk());
        Assert.True(tray.Desktop.LockEntered.Wait(TimeSpan.FromSeconds(10)));

        var request = new JsonObject { ["op"] = op, ["deviceId"] = deviceId.ToString("D") };
        if (op == "devices.permissions")
        {
            request["permissions"] = new JsonArray("automation", "monitoring"); // power removed
        }
        var change = AdminAsync(request);
        await Task.Delay(300);
        Assert.False(change.IsCompleted); // waits for the step that already started
        hold.Set();
        Assert.True((await change.WaitAsync(TimeSpan.FromSeconds(10))).IsOk());

        await _host.Services.Automations!.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Sleep", _host.Power!.Calls);
        var entry = _host.Services.History.List(5, null).First(e => e.Kind == "automation");
        Assert.False(entry.Ok);
    }

    [Fact]
    public async Task Administrator_assigns_an_ownerless_rule_only_to_a_verified_device()
    {
        var (b, bId, bKey) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = b;
        using var __ = bKey;
        Assert.True((await b.RequestAsync("automations.upsert", Upsert("""{"id":"night","name":"Night","trigger":{"type":"time","time":"23:00"},"actions":[{"type":"lock"}]}"""))).IsOk());
        Assert.True((await AdminAsync(new JsonObject { ["op"] = "devices.revoke", ["deviceId"] = bId.ToString("D") })).IsOk());

        var (a, aId, aKey) = await ClientWith(Permission.Automation | Permission.Monitoring);
        await using var ___ = a;
        using var ____ = aKey;
        var owners = await AdminAsync(new JsonObject { ["op"] = "automations.owners" });
        var listed = Assert.Single(owners["automations"]!.AsArray());
        Assert.Equal("night", listed!["automationId"]!.GetValue<string>());

        var devices = await AdminAsync(new JsonObject { ["op"] = "devices.list" });
        var fingerprint = devices["devices"]!.AsArray().Single(d => d!["deviceId"]!.GetValue<string>() == aId.ToString("D"))!["fingerprint"]!.GetValue<string>();
        Assert.Equal(DeviceCrypto.DeviceKeyFingerprint(aKey.ExportSubjectPublicKeyInfo()), fingerprint);
        Assert.Matches("^[0-9A-F]{16}$", fingerprint);

        JsonObject Assign(Guid device, string fp) => new() { ["op"] = "automations.assign", ["automationId"] = "night", ["deviceId"] = device.ToString("D"), ["fingerprint"] = fp };
        var stored = AutomationJson.ToJson(_host.Services.Automations!.Find("night")!).ToJsonString();

        var wrong = await AdminAsync(Assign(aId, "0000000000000000"));
        Assert.False(wrong.IsOk());
        Assert.Contains("fingerprint", wrong["error"]!.GetValue<string>(), StringComparison.Ordinal);
        var unpaired = await AdminAsync(Assign(Guid.NewGuid(), fingerprint));
        Assert.Contains("not paired", unpaired["error"]!.GetValue<string>(), StringComparison.Ordinal);
        var revoked = await AdminAsync(Assign(bId, DeviceCrypto.DeviceKeyFingerprint(bKey.ExportSubjectPublicKeyInfo())));
        Assert.Contains("not paired", revoked["error"]!.GetValue<string>(), StringComparison.Ordinal);
        var weak = await AdminAsync(Assign(aId, fingerprint));
        Assert.Contains("power", weak["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(stored, AutomationJson.ToJson(_host.Services.Automations.Find("night")!).ToJsonString());
        Assert.Empty((await a.RequestAsync("automations.list"))["payload"]!["automations"]!.AsArray());

        _host.Services.Devices.SetPermissions(aId, Permissions.DefaultAfterPairing | Permission.Automation);
        // The file cannot be written (a folder is where the temporary copy goes): clear error, rule unchanged, audited.
        var blocker = Path.Combine(_host.DataDirectory, "automations.json.tmp");
        Directory.CreateDirectory(blocker);
        var unsaved = await AdminAsync(Assign(aId, fingerprint));
        Directory.Delete(blocker);
        Assert.False(unsaved.IsOk());
        Assert.Contains("could not save", unsaved["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(stored, AutomationJson.ToJson(_host.Services.Automations.Find("night")!).ToJsonString());
        Assert.Empty((await a.RequestAsync("automations.list"))["payload"]!["automations"]!.AsArray());
        var saveFailure = File.ReadAllLines(_host.Services.Audit.FilePath).Last(l => l.Contains("\"automations.assign\"", StringComparison.Ordinal));
        Assert.Contains("\"ok\":false", saveFailure, StringComparison.Ordinal);
        Assert.Contains("could not save", saveFailure, StringComparison.Ordinal);
        Assert.Contains("\"automationId\":\"night\"", saveFailure, StringComparison.Ordinal);

        var grouped = string.Join("-", fingerprint.ToLowerInvariant().Chunk(4).Select(c => new string(c)));
        Assert.True((await AdminAsync(Assign(aId, grouped))).IsOk());
        Assert.Equal(aId, Guid.Parse(_host.Services.Automations.Find("night")!.CreatedBy!));
        Assert.Equal("night", (await a.RequestAsync("automations.list"))["payload"]!["automations"]![0]!["id"]!.GetValue<string>());
        Assert.Empty((await AdminAsync(new JsonObject { ["op"] = "automations.owners" }))["automations"]!.AsArray());
        Assert.Contains("already has an owner", (await AdminAsync(Assign(aId, fingerprint)))["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var audit = File.ReadAllLines(_host.Services.Audit.FilePath).Where(l => l.Contains("\"automations.assign\"", StringComparison.Ordinal)).ToList();
        Assert.Contains(audit, l => l.Contains("\"ok\":true", StringComparison.Ordinal) && l.Contains(aId.ToString("D"), StringComparison.Ordinal)
            && l.Contains("\"automationId\":\"night\"", StringComparison.Ordinal) && l.Contains($"\"formerOwner\":\"{bId:D}\"", StringComparison.Ordinal));
        Assert.True(audit.Count(l => l.Contains("\"ok\":false", StringComparison.Ordinal)) >= 5);
    }

    [Fact]
    public async Task Owner_ids_are_compared_as_guids_and_need_a_paired_device_with_a_valid_key()
    {
        var (client, deviceId, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;
        var automations = _host.Services.Automations!;
        var rule = new AutomationDefinition("x", "X", true, new AutomationTrigger(TriggerKind.PcStarted), null, [], deviceId.ToString("N").ToUpperInvariant());
        Assert.True(automations.IsConfirmedOwner(rule, deviceId.ToString("D")));
        Assert.True(automations.IsConfirmedOwner(rule, deviceId.ToString("B")));
        Assert.True(automations.HasConfirmedOwner(rule));
        Assert.False(automations.IsConfirmedOwner(rule, Guid.NewGuid().ToString("D")));
        Assert.False(automations.IsConfirmedOwner(rule, "not-a-guid"));
        Assert.False(automations.IsConfirmedOwner(rule, null));
        Assert.False(automations.HasConfirmedOwner(rule with { CreatedBy = null }));
        Assert.False(automations.HasConfirmedOwner(rule with { CreatedBy = Guid.Empty.ToString("D") }));

        // A record whose key is not a valid P-256 key is not an owner (damaged or foreign devices.json entry).
        var broken = Guid.NewGuid();
        _host.Services.Devices.Add(new DeviceRecord(broken, "Broken", [1, 2, 3], Permissions.DefaultAfterPairing | Permission.Automation, DateTimeOffset.UtcNow));
        Assert.False(automations.IsConfirmedOwner(rule with { CreatedBy = broken.ToString("D") }, broken.ToString("D")));
    }

    [Fact]
    public async Task Sign_in_and_hot_gpu_trigger_rules()
    {
        var (client, _, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;
        await client.RequestAsync("automations.upsert", Upsert("""{"id":"hello","name":"Hello","trigger":{"type":"userSignedIn"},"actions":[{"type":"lock"}]}"""));
        await client.RequestAsync("automations.upsert", Upsert("""{"id":"hot","name":"Hot","trigger":{"type":"metric","metric":"gpuTemperature","above":55},"actions":[{"type":"app","appId":"steam"}]}"""));

        await using var tray = await _host.ConnectTrayAsync();
        tray.Store.AddApp("Steam", Nexus.Core.Apps.AppLaunchKind.Executable, "steam.exe", false, null);
        await AgentIntegrationTests.WaitUntil(() => tray.Desktop.Calls.Contains("lock"));

        _host.Services.Metrics.SampleNow(); // the fake GPU reports 60 °C
        _host.Services.Automations!.Tick();
        await AgentIntegrationTests.WaitUntil(() => tray.Desktop.Calls.Contains("launch:steam"));
        _host.Services.Automations.Tick();
        await _host.Services.Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(tray.Desktop.Calls, c => c == "launch:steam");
    }

    [Fact]
    public async Task Rules_survive_an_agent_restart()
    {
        var (client, _, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using (client)
        using (key)
        {
            await client.RequestAsync("automations.upsert", Upsert("""{"id":"keep","name":"Keep","trigger":{"type":"time","time":"07:15","days":[1]},"actions":[{"type":"lock"}]}"""));
        }
        var dir = _host.DataDirectory;
        await _host.DisposeAsync();
        _host = await AgentTestHost.StartAsync(dataDirectory: dir, metrics: new FakeMetricsSource());
        var rule = Assert.Single(_host.Services.Automations!.Rules);
        Assert.Equal(420 + 15, rule.Trigger.MinuteOfDay);
    }
}

public sealed class CapabilityTests
{
    [Fact]
    public void Every_catalog_command_is_implemented_or_forwarded_to_the_tray()
    {
        var known = Nexus.Agent.Server.CommandDispatcher.Implemented.Concat(Nexus.Agent.Server.CommandDispatcher.ViaTray).ToHashSet(StringComparer.Ordinal);
        var missing = CommandCatalog.V1.All.Select(c => c.Type).Where(t => !known.Contains(t)).ToList();
        Assert.Empty(missing);
    }
}

