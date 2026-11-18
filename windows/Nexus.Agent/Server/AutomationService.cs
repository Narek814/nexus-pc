using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Core.Automation;
using Nexus.Core.Power;
using Nexus.Core.Protocol;
using Nexus.Core.Security;
using Nexus.Core.Session;
using Nexus.Notifications;

namespace Nexus.Agent.Server;

/// <summary>
/// Runs automations on the PC (P6), with or without a phone connected. Every action is one of the
/// fixed kinds in <see cref="AutomationRules"/>; each run re-checks that the device that created the
/// rule still exists and still holds the permissions its actions need (revoking a phone stops its rules).
/// </summary>
internal sealed partial class AutomationService : BackgroundService
{
    public static readonly TimeSpan PcStartedDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SignInDebounce = TimeSpan.FromMinutes(10);

    private readonly AgentServices _services;
    private readonly ILogger<AutomationService> _logger;
    private readonly string _path;
    private readonly AutomationEngine _engine;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, bool Ok, string Summary)> _lastRun = new(StringComparer.Ordinal);
    private List<AutomationDefinition> _rules;
    private DateTimeOffset _lastSignIn = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEvaluated;
    private CancellationToken _stopping;

    public AutomationService(AgentServices services, string dataDirectory, TimeZoneInfo timeZone, ILogger<AutomationService> logger)
    {
        _services = services;
        _logger = logger;
        _path = Path.Combine(dataDirectory, "automations.json");
        _engine = new AutomationEngine(timeZone);
        _rules = Load(_path);
        services.Trays.Connected += (_, _) => OnUserSignedIn();
    }

    public IReadOnlyList<AutomationDefinition> Rules
    {
        get
        {
            lock (_lock)
            {
                return [.. _rules];
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            await Task.Delay(PcStartedDelay, _services.Time, stoppingToken).ConfigureAwait(false);
            foreach (var rule in _engine.ForEvent(Rules, TriggerKind.PcStarted, _services.Time.GetUtcNow()))
            {
                Start(rule, "ПК включился");
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _services.Time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Tick();
                }
#pragma warning disable CA1031 // Automations must never stop the agent.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogTickFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One evaluation (also called by tests): time rules, and metric rules on a new sample.</summary>
    public void Tick()
    {
        AutomationReadings? readings = null;
        if (_services.Metrics.Latest is { } s && s.Timestamp > _lastEvaluated)
        {
            _lastEvaluated = s.Timestamp;
            var gpu = s.Gpus.Select(g => g.Temperature).Where(t => t is not null).Max();
            readings = new AutomationReadings(gpu, s.Cpu.Usage, s.Memory.Usage, s.Cpu.Temperature);
        }
        foreach (var rule in _engine.Due(Rules, _services.Time.GetUtcNow(), readings))
        {
            Start(rule, rule.Trigger.Kind == TriggerKind.Time ? "по расписанию" : "по датчику");
        }
    }

    private void OnUserSignedIn()
    {
        var now = _services.Time.GetUtcNow();
        lock (_lock)
        {
            // The tray also reconnects after an agent restart: count one sign-in per 10 minutes.
            if (now - _lastSignIn < SignInDebounce)
            {
                return;
            }
            _lastSignIn = now;
        }
        foreach (var rule in _engine.ForEvent(Rules, TriggerKind.UserSignedIn, now))
        {
            Start(rule, "вход в Windows");
        }
    }

    public string? Upsert(AutomationDefinition automation)
    {
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Id == automation.Id);
            if (index < 0 && _rules.Count >= AutomationRules.MaxAutomations)
            {
                return $"Не больше {AutomationRules.MaxAutomations} автоматизаций.";
            }
            if (index >= 0)
            {
                _rules[index] = automation;
            }
            else
            {
                _rules.Add(automation);
            }
            Save();
        }
        if (!automation.Enabled)
        {
            Stop(automation.Id);
        }
        return null;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _rules.RemoveAll(r => r.Id == id) > 0;
            if (removed)
            {
                Save();
            }
        }
        Stop(id);
        return removed;
    }

    public AutomationDefinition? Find(string id)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == id);
        }
    }

    /// <returns>False if it is already running.</returns>
    public bool Start(AutomationDefinition rule, string reason)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        lock (_lock)
        {
            if (!_running.TryAdd(rule.Id, cts))
            {
                cts.Dispose();
                return false;
            }
        }
        _ = Task.Run(() => RunAsync(rule, reason, cts), CancellationToken.None);
        return true;
    }

    public bool Stop(string id)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(id, out var cts))
            {
                cts.Cancel();
                return true;
            }
            return false;
        }
    }

    /// <summary>Waits until no automation is running (tests).</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            lock (_lock)
            {
                if (_running.Count == 0)
                {
                    return;
                }
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    public JsonObject ToJson()
    {
        var list = new JsonArray();
        lock (_lock)
        {
            foreach (var rule in _rules)
            {
                var json = AutomationJson.ToJson(rule);
                json.Remove("createdBy");
                if (rule.CreatedBy is not null && Guid.TryParse(rule.CreatedBy, out var device))
                {
                    json["createdByName"] = _services.Devices.Find(device)?.Name ?? "отозванное устройство";
                }
                json["running"] = _running.ContainsKey(rule.Id);
                if (_lastRun.TryGetValue(rule.Id, out var last))
                {
                    json["lastRun"] = new JsonObject
                    {
                        ["ts"] = last.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                        ["ok"] = last.Ok,
                        ["summary"] = last.Summary,
                    };
                }
                list.Add(json);
            }
        }
        return new JsonObject { ["automations"] = list, ["timeZone"] = TimeZoneInfo.Local.Id };
    }

    private async Task RunAsync(AutomationDefinition rule, string reason, CancellationTokenSource cts)
    {
        var failures = new List<string>();
        var done = 0;
        var stopped = false;
        try
        {
            foreach (var action in rule.Actions)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (CreatorProblem(rule, action) is { } problem)
                {
                    failures.Add(problem);
                    break;
                }
                var error = await ExecuteAsync(rule, action, cts.Token).ConfigureAwait(false);
                if (error is null)
                {
                    done++;
                }
                else
                {
                    failures.Add($"{Describe(action)}: {error}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
#pragma warning disable CA1031 // A failing action is reported in history, not thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add("ошибка: " + ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(rule.Id);
            }
            cts.Dispose();
        }

        var ok = failures.Count == 0 && !stopped;
        var summary = stopped ? $"остановлена ({done} из {rule.Actions.Count})"
            : ok ? $"выполнено ({reason})"
            : $"с ошибками ({reason}): {string.Join("; ", failures)}";
        lock (_lock)
        {
            _lastRun[rule.Id] = (_services.Time.GetUtcNow(), ok, summary);
        }
        _services.History.Record("automation", $"Автоматизация «{rule.Name}»: {(stopped ? "остановлена" : ok ? "выполнена" : "с ошибками")}", ok, summary);
    }

    private string? CreatorProblem(AutomationDefinition rule, AutomationAction action)
    {
        var required = AutomationRules.RequiredFor(action) | Permission.Automation;
        if (rule.CreatedBy is null || !Guid.TryParse(rule.CreatedBy, out var id) || _services.Devices.Find(id) is not { } device)
        {
            return "устройство, создавшее автоматизацию, отозвано";
        }
        return (device.Permissions & required) == required ? null : $"у устройства «{device.Name}» больше нет нужного разрешения";
    }

    private async Task<string?> ExecuteAsync(AutomationDefinition rule, AutomationAction action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(action.Seconds!.Value), _services.Time, ct).ConfigureAwait(false);
                return null;
            case "profile":
                var run = await Tray(SessionOps.ProfilesRun, new JsonObject { ["profileId"] = action.Id }, ct).ConfigureAwait(false);
                if (run is null && _services.Rgb?.Find(action.Id!) is { } linked && HasCreatorPermission(rule, Permission.Rgb))
                {
                    try
                    {
                        await _services.Rgb.ApplyAsync(linked.Look, null, ct).ConfigureAwait(false);
                    }
                    catch (RgbService.RgbUnavailableException)
                    {
                        // Programs started; lighting is optional here.
                    }
                }
                return run;
            case "app":
                return await Tray(SessionOps.AppsLaunch, new JsonObject { ["appId"] = action.Id }, ct).ConfigureAwait(false);
            case "lock":
                return await Tray(SessionOps.Lock, [], ct).ConfigureAwait(false);
            case "rgb" or "rgbProfile":
                if (_services.Rgb is not { } rgb)
                {
                    return "подсветка недоступна";
                }
                var look = action.Kind == "rgb"
                    ? new RgbLook(action.Color!, action.Brightness!.Value, action.Effect!)
                    : rgb.Find(action.Id!)?.Look;
                if (look is null)
                {
                    return $"нет профиля подсветки «{action.Id}»";
                }
                try
                {
                    var result = await rgb.ApplyAsync(look, null, ct).ConfigureAwait(false);
                    return result["applied"]!.GetValue<int>() > 0 ? null : "ни одно устройство подсветки не ответило";
                }
                catch (RgbService.RgbUnavailableException)
                {
                    return "OpenRGB не запущен";
                }
            case "sleep":
                var sleep = _services.Power.Sleep();
                return sleep.Ok ? null : sleep.Message;
            case "shutdown" or "restart":
                return await PowerAsync(rule, action, ct).ConfigureAwait(false);
            case "notify":
                if (_services.Alerts is not { } alerts)
                {
                    return "уведомления недоступны";
                }
                return await alerts.NotifyAsync(rule.Name, action.Message!, ct).ConfigureAwait(false)
                    ? null
                    : "не доставлено (ntfy выключен или нет интернета)";
            default:
                return "неизвестное действие";
        }
    }

    private async Task<string?> PowerAsync(AutomationDefinition rule, AutomationAction action, CancellationToken ct)
    {
        var verb = action.Kind;
        var delay = action.Seconds!.Value;
        var reason = $"Nexus PC: автоматизация «{rule.Name}».";
        if (_services.Trays.IsAvailable)
        {
            var viaTray = await _services.Trays.CallAsync(SessionOps.PowerSchedule, new JsonObject
            {
                ["action"] = verb,
                ["delaySeconds"] = delay,
                ["force"] = false,
                ["reason"] = reason,
            }, ct).ConfigureAwait(false);
            if (viaTray.Ok)
            {
                return null;
            }
        }
        var outcome = _services.Power.Schedule(verb == "restart" ? PowerAction.Restart : PowerAction.Shutdown,
            TimeSpan.FromSeconds(Math.Max(delay, CommandDispatcher.MinGraceSeconds)), force: false, reason);
        return outcome.Ok ? null : outcome.Message;
    }

    private async Task<string?> Tray(string op, JsonObject args, CancellationToken ct)
    {
        var result = await _services.Trays.CallAsync(op, args, ct).ConfigureAwait(false);
        return result.Ok ? null : result.ErrorCode == ErrorCodes.SessionUnavailable ? "никто не вошёл в Windows (трей не запущен)" : result.Message;
    }

    private bool HasCreatorPermission(AutomationDefinition rule, Permission permission) =>
        rule.CreatedBy is not null && Guid.TryParse(rule.CreatedBy, out var id)
        && _services.Devices.Find(id) is { } device && device.Permissions.HasFlag(permission);

    private static string Describe(AutomationAction action) => action.Kind switch
    {
        "profile" => $"профиль «{action.Id}»",
        "app" => $"программа «{action.Id}»",
        "rgb" => "подсветка",
        "rgbProfile" => $"подсветка «{action.Id}»",
        "lock" => "блокировка",
        "sleep" => "сон",
        "shutdown" => "выключение",
        "restart" => "перезагрузка",
        "notify" => "уведомление",
        _ => action.Kind,
    };

    private void Save()
    {
        var json = new JsonArray([.. _rules.Select(r => (JsonNode)AutomationJson.ToJson(r))]);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }

    private static List<AutomationDefinition> Load(string path)
    {
        var rules = new List<AutomationDefinition>();
        if (!File.Exists(path))
        {
            return rules;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (AutomationJson.Parse(item, null, out var rule, out _))
                {
                    rules.Add(rule!);
                }
            }
        }
        catch (JsonException)
        {
            // A damaged file must not stop the agent; rules can be recreated from the phone.
        }
        return rules;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
