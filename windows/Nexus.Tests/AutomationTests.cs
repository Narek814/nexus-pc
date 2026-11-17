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
        var rule = Rule("""{"id":"hot","name":"Hot","trigger":{"type":"metric","metric":"gpuTemperature","above":85,"forSeconds":30},"actions":[{"type":"notify","message":"hot"}]}""");
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
                        {"type":"wait","seconds":600},{"type":"shutdown","delaySeconds":300},{"type":"notify","message":"Спокойной ночи"}]}
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
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"actions":[{"type":"notify","message":"a\nb"}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"actions":[{"type":"app","appId":"C:\\evil.exe"}]}""")]
    [InlineData("""{"id":"x","name":"X","trigger":{"type":"pcStarted"},"onlyBetween":{"from":"10:00","to":"10:00"},"actions":[{"type":"lock"}]}""")]
    public void Rejects(string json) => Assert.False(AutomationJson.Parse(JsonElement.Parse(json), "dev", out _, out _));
}

public sealed class AutomationProtocolTests : IAsyncLifetime
{
    private readonly FakeNotifier _notifier = new();
    private AgentTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync(metrics: new FakeMetricsSource(), notifier: _notifier);

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
             "actions":[{"type":"profile","profileId":"gaming"},{"type":"wait","seconds":1},{"type":"notify","message":"Готово к игре"},{"type":"lock"}]}
            """));
        Assert.True(saved.IsOk(), saved.ToJsonString());
        Assert.True((await client.RequestAsync("automations.run", new JsonObject { ["automationId"] = "play" })).IsOk());
        Assert.Equal(ErrorCodes.AutomationBusy, (await client.RequestAsync("automations.run", new JsonObject { ["automationId"] = "play" })).ErrorCode());
        await _host.Services.Automations!.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("launch:steam", tray.Desktop.Calls);
        Assert.Contains("lock", tray.Desktop.Calls);
        Assert.Contains(_notifier.Sent, a => a.Message == "Готово к игре");

        var list = await client.RequestAsync("automations.list");
        var lastRun = list["payload"]!["automations"]![0]!["lastRun"]!;
        // ntfy is off by default: the notification step is reported, the rest ran.
        Assert.False(lastRun["ok"]!.GetValue<bool>());
        Assert.Contains("уведомление", lastRun["summary"]!.GetValue<string>(), StringComparison.Ordinal);

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

    [Fact]
    public async Task Revoking_the_creator_stops_its_rules_from_acting()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (client, deviceId, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;
        await client.RequestAsync("automations.upsert", Upsert("""{"id":"lock","name":"Lock","trigger":{"type":"pcStarted"},"actions":[{"type":"lock"}]}"""));

        _host.Services.Devices.Remove(deviceId);
        Assert.True(_host.Services.Automations!.Start(_host.Services.Automations.Find("lock")!, "test"));
        await _host.Services.Automations.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("lock", tray.Desktop.Calls);
        var entry = _host.Services.History.List(5, null).First(e => e.Kind == "automation");
        Assert.False(entry.Ok);
        Assert.Contains("отозвано", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_and_hot_gpu_trigger_rules()
    {
        var (client, _, key) = await ClientWith(Permissions.DefaultAfterPairing | Permission.Automation);
        await using var _ = client;
        using var __ = key;
        await client.RequestAsync("automations.upsert", Upsert("""{"id":"hello","name":"Hello","trigger":{"type":"userSignedIn"},"actions":[{"type":"notify","message":"вход"}]}"""));
        await client.RequestAsync("automations.upsert", Upsert("""{"id":"hot","name":"Hot","trigger":{"type":"metric","metric":"gpuTemperature","above":55},"actions":[{"type":"notify","message":"горячо"}]}"""));

        await using (await _host.ConnectTrayAsync())
        {
            await AgentIntegrationTests.WaitUntil(() => _notifier.Sent.Any(a => a.Message == "вход"));
        }

        _host.Services.Metrics.SampleNow(); // the fake GPU reports 60 °C
        _host.Services.Automations!.Tick();
        await AgentIntegrationTests.WaitUntil(() => _notifier.Sent.Any(a => a.Message == "горячо"));
        Assert.Single(_notifier.Sent, a => a.Message == "горячо");
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
        _host = await AgentTestHost.StartAsync(dataDirectory: dir, metrics: new FakeMetricsSource(), notifier: _notifier);
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

