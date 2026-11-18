using System.Globalization;
using Nexus.Monitoring;

namespace Nexus.Notifications;

public enum AlertSeverity
{
    Info,
    Warning,
}

/// <summary>What to tell the person. Text holds only the PC name, event and numbers — no paths, apps or user data.</summary>
public sealed record AlertEvent(string Key, string Title, string Message, AlertSeverity Severity);

/// <summary>
/// Turns metric samples into alerts without noise: a condition must hold for a while (spikes are
/// ignored), it re-arms only after the value falls back by a margin (hysteresis), and the same
/// alert is not repeated within the cooldown.
/// </summary>
public sealed class AlertEngine
{
    public static readonly TimeSpan GpuHold = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UsageHold = TimeSpan.FromMinutes(5);
    private const double Margin = 5;

    private readonly TimeProvider _time;
    private readonly Dictionary<string, Condition> _conditions = [];
    private readonly Dictionary<string, DateTimeOffset> _lastSent = [];

    public AlertEngine(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public IReadOnlyList<AlertEvent> Evaluate(MetricsSnapshot snapshot, AlertSettings settings, string pcName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        var now = _time.GetUtcNow();
        var events = new List<AlertEvent>();

        if (settings.GpuTemperatureEnabled)
        {
            foreach (var gpu in snapshot.Gpus)
            {
                Check($"gpu.temperature:{gpu.Name}", gpu.Temperature, settings.GpuTemperatureC, GpuHold, now, settings, events,
                    v => new AlertEvent($"gpu.temperature:{gpu.Name}", $"{pcName}: видеокарта горячая", $"{gpu.Name}: {v:0}°C (порог {settings.GpuTemperatureC}°C).", AlertSeverity.Warning));
            }
        }
        if (settings.CpuTemperatureEnabled)
        {
            Check("cpu.temperature", snapshot.Cpu.Temperature, settings.CpuTemperatureC, GpuHold, now, settings, events,
                v => new AlertEvent("cpu.temperature", $"{pcName}: процессор горячий", $"{v:0}°C (порог {settings.CpuTemperatureC}°C).", AlertSeverity.Warning));
        }
        if (settings.CpuUsageEnabled)
        {
            Check("cpu.usage", snapshot.Cpu.Usage, settings.CpuUsagePercent, UsageHold, now, settings, events,
                v => new AlertEvent("cpu.usage", $"{pcName}: процессор загружен", $"{v:0}% дольше {UsageHold.TotalMinutes:0} минут.", AlertSeverity.Warning));
        }
        if (settings.MemoryUsageEnabled)
        {
            Check("memory.usage", snapshot.Memory.Usage, settings.MemoryUsagePercent, UsageHold, now, settings, events,
                v => new AlertEvent("memory.usage", $"{pcName}: мало свободной памяти", $"Занято {v:0}% дольше {UsageHold.TotalMinutes:0} минут.", AlertSeverity.Warning));
        }
        if (settings.DiskFreeEnabled)
        {
            foreach (var disk in snapshot.Disks)
            {
                var freePercent = disk.TotalBytes > 0 ? 100.0 * disk.FreeBytes / disk.TotalBytes : (double?)null;
                // "Low free space" is the same check on the used share: used ≥ 100 − free%.
                Check($"disk:{disk.Name}", freePercent is { } f ? 100 - f : null, 100 - settings.DiskFreePercent, TimeSpan.Zero, now, settings, events,
                    _ => new AlertEvent($"disk:{disk.Name}", $"{pcName}: мало места на диске",
                        $"{disk.Name} свободно {FormatGb(disk.FreeBytes)} ({freePercent:0}%).", AlertSeverity.Warning));
            }
        }
        return events;
    }

    /// <summary>Power/connection events (not metric-based): still deduplicated by the cooldown.</summary>
    public AlertEvent? PowerEvent(string key, string title, string message, AlertSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.PowerEventsEnabled)
        {
            return null;
        }
        var now = _time.GetUtcNow();
        if (_lastSent.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1))
        {
            return null; // the same power event twice within a minute is a duplicate
        }
        _lastSent[key] = now;
        return new AlertEvent(key, title, message, AlertSeverity.Info);
    }

    private void Check(string key, double? value, double threshold, TimeSpan hold, DateTimeOffset now, AlertSettings settings,
        List<AlertEvent> events, Func<double, AlertEvent> create)
    {
        if (value is not { } v)
        {
            return; // unavailable values never trigger or clear anything
        }
        var condition = _conditions.TryGetValue(key, out var existing) ? existing : _conditions[key] = new Condition();

        if (v >= threshold)
        {
            condition.Since ??= now;
            if (!condition.Fired && now - condition.Since.Value >= hold && !InCooldown(key, now, settings))
            {
                condition.Fired = true;
                _lastSent[key] = now;
                events.Add(create(v));
            }
        }
        else if (v < threshold - Margin)
        {
            condition.Since = null;
            condition.Fired = false; // re-armed only after a clear drop
        }
        else
        {
            condition.Since = condition.Fired ? condition.Since : null; // in the margin: keep a fired alert armed-off
        }
    }

    private bool InCooldown(string key, DateTimeOffset now, AlertSettings settings) =>
        _lastSent.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(settings.CooldownMinutes);

    private static string FormatGb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " ГБ";

    private sealed class Condition
    {
        public DateTimeOffset? Since { get; set; }
        public bool Fired { get; set; }
    }
}
