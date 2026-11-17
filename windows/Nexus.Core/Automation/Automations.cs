using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Core.Automation;

public enum TriggerKind { Time, Metric, PcStarted, UserSignedIn }

/// <summary>When a rule fires. Time = local time of the PC; Days use ISO numbers (1 = Monday … 7 = Sunday).</summary>
public sealed record AutomationTrigger(
    TriggerKind Kind,
    int? MinuteOfDay = null,
    IReadOnlyList<int>? Days = null,
    string? Metric = null,
    int? Above = null,
    int? ForSeconds = null);

/// <summary>One step. Only these kinds exist: an automation can never run a command line or a path.</summary>
public sealed record AutomationAction(
    string Kind,
    string? Id = null,
    string? Color = null,
    int? Brightness = null,
    string? Effect = null,
    int? Seconds = null,
    string? Message = null);

public sealed record AutomationDefinition(
    string Id,
    string Name,
    bool Enabled,
    AutomationTrigger Trigger,
    (int From, int To)? OnlyBetween,
    IReadOnlyList<AutomationAction> Actions,
    string? CreatedBy);

public sealed record AutomationUpsertPayload(AutomationDefinition Automation);

public sealed record AutomationRefPayload(string AutomationId);

public sealed record HistoryListPayload(int Limit, DateTimeOffset? Before);

public static class AutomationRules
{
    public const int MaxAutomations = 32;
    public const int MaxActions = 8;
    public const int MinPowerDelaySeconds = 60;
    public const int MaxWaitSeconds = 4 * 3600;
    public const int Hysteresis = 5;

    public static readonly IReadOnlyList<string> Metrics = ["gpuTemperature", "cpuUsage", "memoryUsage"];

    public static readonly IReadOnlyList<string> ActionKinds =
        ["profile", "app", "rgb", "rgbProfile", "lock", "sleep", "shutdown", "restart", "notify", "wait"];

    /// <summary>What a device must itself be allowed to do to create (and keep running) an action: no escalation.</summary>
    public static Permission RequiredFor(AutomationAction action) => action.Kind switch
    {
        "profile" or "app" => Permission.Apps,
        "rgb" or "rgbProfile" => Permission.Rgb,
        "lock" or "sleep" or "shutdown" or "restart" => Permission.Power,
        "notify" => Permission.Monitoring,
        _ => Permission.None,
    };

    public static Permission RequiredFor(AutomationDefinition automation) =>
        automation.Actions.Aggregate(Permission.Automation, (all, a) => all | RequiredFor(a));

    public static bool NeedsConfirmation(AutomationDefinition automation) =>
        automation.Actions.Any(a => a.Kind is "shutdown" or "restart");
}

/// <summary>Wire/storage format (docs/API.md «Автоматизации»).</summary>
public static class AutomationJson
{
    public static PayloadResult Upsert(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(p => p.Name != "automation")
            || !payload.TryGetProperty("automation", out var a))
        {
            return PayloadResult.Invalid("'automation' is required.");
        }
        return Parse(a, createdBy: null, out var automation, out var error)
            ? PayloadResult.Ok(new AutomationUpsertPayload(automation!))
            : PayloadResult.Invalid(error!);
    }

    public static PayloadResult Ref(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.EnumerateObject().All(p => p.Name == "automationId")
        && payload.TryGetProperty("automationId", out var id) && id.ValueKind == JsonValueKind.String && Identifiers.IsSlug(id.GetString())
            ? PayloadResult.Ok(new AutomationRefPayload(id.GetString()!))
            : PayloadResult.Invalid("'automationId' must be a slug.");

    public static PayloadResult HistoryList(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(p => p.Name is not ("limit" or "before")))
        {
            return PayloadResult.Invalid("Only 'limit' and 'before' are allowed.");
        }
        var limit = 100;
        if (payload.TryGetProperty("limit", out var l) && (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out limit) || limit is < 1 or > 200))
        {
            return PayloadResult.Invalid("'limit' must be 1..200.");
        }
        DateTimeOffset? before = null;
        if (payload.TryGetProperty("before", out var b))
        {
            if (b.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(b.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return PayloadResult.Invalid("'before' must be an ISO 8601 time.");
            }
            before = parsed;
        }
        return PayloadResult.Ok(new HistoryListPayload(limit, before));
    }

    public static bool Parse(JsonElement a, string? createdBy, out AutomationDefinition? automation, out string? error)
    {
        automation = null;
        error = null;
        string[] allowed = ["id", "name", "enabled", "trigger", "onlyBetween", "actions", "createdBy"];
        if (a.ValueKind != JsonValueKind.Object || a.EnumerateObject().Any(p => !allowed.Contains(p.Name)))
        {
            error = "Automation has unknown fields.";
            return false;
        }
        if (!Str(a, "id", out var id) || !Identifiers.IsSlug(id) || !Str(a, "name", out var name) || !Identifiers.IsValidDisplayName(name))
        {
            error = "'id' must be a slug and 'name' 1..64 characters.";
            return false;
        }
        var enabled = !a.TryGetProperty("enabled", out var en) || en.ValueKind == JsonValueKind.True;
        if (a.TryGetProperty("enabled", out en) && en.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = "'enabled' must be true or false.";
            return false;
        }
        if (!a.TryGetProperty("trigger", out var t) || ParseTrigger(t) is not { } trigger)
        {
            error = "Invalid 'trigger'.";
            return false;
        }
        (int, int)? between = null;
        if (a.TryGetProperty("onlyBetween", out var ob))
        {
            if (ob.ValueKind != JsonValueKind.Object || ob.EnumerateObject().Any(p => p.Name is not ("from" or "to"))
                || !Str(ob, "from", out var from) || ParseTime(from) is not { } f
                || !Str(ob, "to", out var to) || ParseTime(to) is not { } tt || f == tt)
            {
                error = "'onlyBetween' needs different 'from' and 'to' as HH:mm.";
                return false;
            }
            between = (f, tt);
        }
        if (!a.TryGetProperty("actions", out var list) || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() is 0 or > AutomationRules.MaxActions)
        {
            error = $"'actions' must have 1..{AutomationRules.MaxActions} steps.";
            return false;
        }
        var actions = new List<AutomationAction>();
        foreach (var item in list.EnumerateArray())
        {
            if (ParseAction(item) is not { } action)
            {
                error = "Invalid action.";
                return false;
            }
            actions.Add(action);
        }
        if (actions.All(x => x.Kind == "wait"))
        {
            error = "An automation needs at least one action besides waiting.";
            return false;
        }
        if (createdBy is null && a.TryGetProperty("createdBy", out var cb) && cb.ValueKind == JsonValueKind.String)
        {
            createdBy = cb.GetString();
        }
        automation = new AutomationDefinition(id, name, enabled, trigger, between, actions, createdBy);
        return true;
    }

    private static AutomationTrigger? ParseTrigger(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object || !Str(t, "type", out var type))
        {
            return null;
        }
        bool Only(params string[] names) => t.EnumerateObject().All(p => p.Name == "type" || names.Contains(p.Name));
        switch (type)
        {
            case "time":
                if (!Only("time", "days") || !Str(t, "time", out var time) || ParseTime(time) is not { } minute)
                {
                    return null;
                }
                var days = new List<int>();
                if (t.TryGetProperty("days", out var d))
                {
                    if (d.ValueKind != JsonValueKind.Array || d.GetArrayLength() is 0 or > 7)
                    {
                        return null;
                    }
                    foreach (var day in d.EnumerateArray())
                    {
                        if (day.ValueKind != JsonValueKind.Number || !day.TryGetInt32(out var n) || n is < 1 or > 7 || days.Contains(n))
                        {
                            return null;
                        }
                        days.Add(n);
                    }
                }
                else
                {
                    days.AddRange([1, 2, 3, 4, 5, 6, 7]);
                }
                days.Sort();
                return new AutomationTrigger(TriggerKind.Time, MinuteOfDay: minute, Days: days);
            case "metric":
                if (!Only("metric", "above", "forSeconds") || !Str(t, "metric", out var metric) || !AutomationRules.Metrics.Contains(metric)
                    || !Int(t, "above", 1, 120, out var above))
                {
                    return null;
                }
                var forSeconds = 0;
                if (t.TryGetProperty("forSeconds", out _) && !Int(t, "forSeconds", 0, 3600, out forSeconds))
                {
                    return null;
                }
                return new AutomationTrigger(TriggerKind.Metric, Metric: metric, Above: above, ForSeconds: forSeconds);
            case "pcStarted":
                return Only() ? new AutomationTrigger(TriggerKind.PcStarted) : null;
            case "userSignedIn":
                return Only() ? new AutomationTrigger(TriggerKind.UserSignedIn) : null;
            default:
                return null;
        }
    }

    private static AutomationAction? ParseAction(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object || !Str(x, "type", out var type))
        {
            return null;
        }
        bool Only(params string[] names) => x.EnumerateObject().All(p => p.Name == "type" || names.Contains(p.Name));
        switch (type)
        {
            case "profile" or "rgbProfile":
                return Only("profileId") && Str(x, "profileId", out var pid) && Identifiers.IsSlug(pid) ? new AutomationAction(type, Id: pid) : null;
            case "app":
                return Only("appId") && Str(x, "appId", out var aid) && Identifiers.IsSlug(aid) ? new AutomationAction(type, Id: aid) : null;
            case "rgb":
                if (!Only("color", "brightness", "effect") || !Str(x, "color", out var color) || color.Length != 7 || color[0] != '#'
                    || !color.Skip(1).All(Uri.IsHexDigit) || !Int(x, "brightness", 0, 100, out var brightness)
                    || !Str(x, "effect", out var effect) || effect.Length is 0 or > 64)
                {
                    return null;
                }
                return new AutomationAction(type, Color: color.ToUpperInvariant(), Brightness: brightness, Effect: effect);
            case "lock" or "sleep":
                return Only() ? new AutomationAction(type) : null;
            case "shutdown" or "restart":
                // At least a minute: Windows shows the countdown and anyone at the PC (or the phone) can cancel.
                return Only("delaySeconds") && Int(x, "delaySeconds", AutomationRules.MinPowerDelaySeconds, 86400, out var delay)
                    ? new AutomationAction(type, Seconds: delay) : null;
            case "notify":
                return Only("message") && Str(x, "message", out var message) && message.Length is > 0 and <= 200 && !message.Any(char.IsControl)
                    ? new AutomationAction(type, Message: message) : null;
            case "wait":
                return Only("seconds") && Int(x, "seconds", 1, AutomationRules.MaxWaitSeconds, out var seconds)
                    ? new AutomationAction(type, Seconds: seconds) : null;
            default:
                return null;
        }
    }

    public static JsonObject ToJson(AutomationDefinition a)
    {
        var trigger = new JsonObject();
        switch (a.Trigger.Kind)
        {
            case TriggerKind.Time:
                trigger["type"] = "time";
                trigger["time"] = FormatTime(a.Trigger.MinuteOfDay!.Value);
                trigger["days"] = new JsonArray([.. a.Trigger.Days!.Select(d => (JsonNode)d)]);
                break;
            case TriggerKind.Metric:
                trigger["type"] = "metric";
                trigger["metric"] = a.Trigger.Metric;
                trigger["above"] = a.Trigger.Above;
                trigger["forSeconds"] = a.Trigger.ForSeconds ?? 0;
                break;
            case TriggerKind.PcStarted:
                trigger["type"] = "pcStarted";
                break;
            default:
                trigger["type"] = "userSignedIn";
                break;
        }
        var json = new JsonObject
        {
            ["id"] = a.Id,
            ["name"] = a.Name,
            ["enabled"] = a.Enabled,
            ["trigger"] = trigger,
            ["actions"] = new JsonArray([.. a.Actions.Select(x => (JsonNode)ActionJson(x))]),
        };
        if (a.OnlyBetween is { } between)
        {
            json["onlyBetween"] = new JsonObject { ["from"] = FormatTime(between.From), ["to"] = FormatTime(between.To) };
        }
        if (a.CreatedBy is not null)
        {
            json["createdBy"] = a.CreatedBy;
        }
        return json;
    }

    private static JsonObject ActionJson(AutomationAction x)
    {
        var json = new JsonObject { ["type"] = x.Kind };
        switch (x.Kind)
        {
            case "profile" or "rgbProfile":
                json["profileId"] = x.Id;
                break;
            case "app":
                json["appId"] = x.Id;
                break;
            case "rgb":
                json["color"] = x.Color;
                json["brightness"] = x.Brightness;
                json["effect"] = x.Effect;
                break;
            case "shutdown" or "restart":
                json["delaySeconds"] = x.Seconds;
                break;
            case "notify":
                json["message"] = x.Message;
                break;
            case "wait":
                json["seconds"] = x.Seconds;
                break;
        }
        return json;
    }

    public static int? ParseTime(string text)
    {
        if (text.Length != 5 || text[2] != ':' || !int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) || h > 23 || m > 59)
        {
            return null;
        }
        return h * 60 + m;
    }

    public static string FormatTime(int minuteOfDay) =>
        string.Create(CultureInfo.InvariantCulture, $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}");

    private static bool Str(JsonElement e, string name, out string value)
    {
        value = "";
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = v.GetString()!;
        return true;
    }

    private static bool Int(JsonElement e, string name, int min, int max, out int value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value) && value >= min && value <= max;
    }
}

/// <summary>Current readings a metric trigger can watch; null = sensor unavailable.</summary>
public sealed record AutomationReadings(double? GpuTemperature, double? CpuUsage, double? MemoryUsage)
{
    public double? Get(string metric) => metric switch
    {
        "gpuTemperature" => GpuTemperature,
        "cpuUsage" => CpuUsage,
        "memoryUsage" => MemoryUsage,
        _ => null,
    };
}

/// <summary>
/// Decides which rules are due. Pure and clock-injected so it can be tested; the agent runs the actions.
/// Time rules fire once per day within 2 minutes after their time (a sleeping PC misses them).
/// Metric rules fire after the value stays at or above the threshold for the hold time, then re-arm
/// only after it drops 5 units below — like Smart Alerts, so a value hovering at the line does not repeat.
/// </summary>
public sealed class AutomationEngine(TimeZoneInfo timeZone)
{
    private static readonly TimeSpan TimeWindow = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateOnly> _firedOn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset? AboveSince, bool Armed)> _metric = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public IReadOnlyList<AutomationDefinition> Due(IEnumerable<AutomationDefinition> rules, DateTimeOffset now, AutomationReadings? readings)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        var due = new List<AutomationDefinition>();
        lock (_lock)
        {
            foreach (var rule in rules)
            {
                if (!rule.Enabled)
                {
                    _metric.Remove(rule.Id);
                    continue;
                }
                var fire = rule.Trigger.Kind switch
                {
                    TriggerKind.Time => TimeDue(rule, local),
                    TriggerKind.Metric => readings is not null && MetricDue(rule, now, readings),
                    _ => false,
                };
                if (fire && Allowed(rule, local))
                {
                    due.Add(rule);
                }
            }
        }
        return due;
    }

    /// <summary>Rules for an event (PC started, user signed in), if their time window allows.</summary>
    public IReadOnlyList<AutomationDefinition> ForEvent(IEnumerable<AutomationDefinition> rules, TriggerKind kind, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        return [.. rules.Where(r => r.Enabled && r.Trigger.Kind == kind && Allowed(r, local))];
    }

    private bool TimeDue(AutomationDefinition rule, DateTimeOffset local)
    {
        var today = DateOnly.FromDateTime(local.DateTime);
        var at = local.Date.AddMinutes(rule.Trigger.MinuteOfDay!.Value);
        var since = local.DateTime - at;
        var isoDay = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
        if (since < TimeSpan.Zero || since >= TimeWindow || !rule.Trigger.Days!.Contains(isoDay)
            || (_firedOn.TryGetValue(rule.Id, out var last) && last == today))
        {
            return false;
        }
        _firedOn[rule.Id] = today;
        return true;
    }

    private bool MetricDue(AutomationDefinition rule, DateTimeOffset now, AutomationReadings readings)
    {
        var state = _metric.GetValueOrDefault(rule.Id, (null, true));
        var value = readings.Get(rule.Trigger.Metric!);
        var above = rule.Trigger.Above!.Value;
        var fire = false;
        if (value is null)
        {
            state.AboveSince = null; // unknown: neither fire nor re-arm
        }
        else if (value >= above)
        {
            state.AboveSince ??= now;
            if (state.Armed && now - state.AboveSince.Value >= TimeSpan.FromSeconds(rule.Trigger.ForSeconds ?? 0))
            {
                fire = true;
                state.Armed = false;
            }
        }
        else
        {
            state.AboveSince = null;
            if (value <= above - AutomationRules.Hysteresis)
            {
                state.Armed = true;
            }
        }
        _metric[rule.Id] = state;
        return fire;
    }

    private static bool Allowed(AutomationDefinition rule, DateTimeOffset local)
    {
        if (rule.OnlyBetween is not { } window)
        {
            return true;
        }
        var minute = local.Hour * 60 + local.Minute;
        return window.From < window.To
            ? minute >= window.From && minute < window.To
            : minute >= window.From || minute < window.To; // e.g. 22:00–07:00
    }
}
