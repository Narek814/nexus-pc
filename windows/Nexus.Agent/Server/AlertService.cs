using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Core.Protocol;
using Nexus.Monitoring;
using Nexus.Notifications;

namespace Nexus.Agent.Server;

/// <summary>
/// Smart Alerts and power events (P4b): evaluates each new metrics sample, keeps a heartbeat to
/// detect unexpected shutdowns, and delivers through ntfy when the owner enabled it.
/// </summary>
internal sealed partial class AlertService : BackgroundService
{
    private readonly MetricsSampler _metrics;
    private readonly INotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertService> _logger;
    private readonly string _settingsPath;
    private readonly string _pcName;
    private readonly AlertEngine _engine;
    private readonly PowerLifecycle _lifecycle;
    private readonly Lock _lock = new();
    private AlertSettings _settings;
    private DateTimeOffset _lastEvaluated;

    public AlertService(MetricsSampler metrics, INotifier notifier, string dataDirectory, string pcName, TimeProvider time, ILogger<AlertService> logger)
    {
        _metrics = metrics;
        _notifier = notifier;
        _time = time;
        _logger = logger;
        _pcName = pcName;
        _settingsPath = Path.Combine(dataDirectory, "alerts.json");
        _settings = AlertSettingsStore.Load(_settingsPath);
        _engine = new AlertEngine(time);
        _lifecycle = new PowerLifecycle(Path.Combine(dataDirectory, "running.marker"), time);
    }

    public RecentAlerts Recent { get; } = new();

    public AlertSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_lifecycle.Start() is { } lastSeen)
        {
            var local = lastSeen.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
            await RaiseAsync(_engine.PowerEvent("power.unexpected", $"{_pcName}: был выключен неожиданно",
                $"Последний признак работы: {local}. Возможно, пропало питание или система зависла.", Settings), stoppingToken).ConfigureAwait(false);
        }
        await RaiseAsync(_engine.PowerEvent("power.on", $"{_pcName} включился", "Nexus PC Agent запущен.", Settings), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(MetricsSampler.Interval);
        var heartbeat = _time.GetUtcNow();
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (_time.GetUtcNow() - heartbeat >= TimeSpan.FromMinutes(1))
                {
                    _lifecycle.Heartbeat();
                    heartbeat = _time.GetUtcNow();
                }
                if (_metrics.Latest is { } snapshot && snapshot.Timestamp > _lastEvaluated)
                {
                    _lastEvaluated = snapshot.Timestamp;
                    foreach (var alert in _engine.Evaluate(snapshot, Settings, _pcName))
                    {
                        await RaiseAsync(alert, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
#pragma warning disable CA1031 // Alerts must never stop the agent.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogAlertFailed(_logger, ex);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _lifecycle.CleanStop();
        // Best effort while Windows shuts down: the network may already be gone.
        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await RaiseAsync(_engine.PowerEvent("power.off", $"{_pcName} выключается", "Nexus PC Agent остановлен (выключение или перезагрузка ПК).", Settings), quick.Token).ConfigureAwait(false);
    }

    /// <summary>A message from an automation (ntfy, if the owner enabled it).</summary>
    public async Task<bool> NotifyAsync(string title, string message, CancellationToken cancellationToken)
    {
        var alert = new AlertEvent("automation", $"{_pcName}: {title}", message, AlertSeverity.Info);
        var delivered = await _notifier.SendAsync(alert, Settings, cancellationToken).ConfigureAwait(false);
        Recent.Add(_time.GetUtcNow(), alert, delivered);
        return delivered;
    }

    /// <summary>Set by the host: alerts also appear in the PC history.</summary>
    public Nexus.Agent.Storage.HistoryStore? History { get; set; }

    public async Task<bool> SendTestAsync(CancellationToken cancellationToken)
    {
        var alert = new AlertEvent("test", $"{_pcName}: тестовое уведомление", "Если вы видите это на iPhone, уведомления Nexus PC работают.", AlertSeverity.Info);
        var delivered = await _notifier.SendAsync(alert, Settings, cancellationToken).ConfigureAwait(false);
        Recent.Add(_time.GetUtcNow(), alert, delivered);
        return delivered;
    }

    /// <summary>Applies a partial update. Returns null on success, otherwise the reason.</summary>
    public string? Apply(AlertsSetPayload change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var s = _settings;
            bool Flag(string key, bool current) => change.Flags.TryGetValue(key, out var v) ? v : current;
            int Number(string key, int current) => change.Numbers.TryGetValue(key, out var v) ? v : current;
            var updated = s with
            {
                GpuTemperatureEnabled = Flag("gpuTemperatureEnabled", s.GpuTemperatureEnabled),
                GpuTemperatureC = Number("gpuTemperatureC", s.GpuTemperatureC),
                CpuTemperatureEnabled = Flag("cpuTemperatureEnabled", s.CpuTemperatureEnabled),
                CpuTemperatureC = Number("cpuTemperatureC", s.CpuTemperatureC),
                CpuUsageEnabled = Flag("cpuUsageEnabled", s.CpuUsageEnabled),
                CpuUsagePercent = Number("cpuUsagePercent", s.CpuUsagePercent),
                MemoryUsageEnabled = Flag("memoryUsageEnabled", s.MemoryUsageEnabled),
                MemoryUsagePercent = Number("memoryUsagePercent", s.MemoryUsagePercent),
                DiskFreeEnabled = Flag("diskFreeEnabled", s.DiskFreeEnabled),
                DiskFreePercent = Number("diskFreePercent", s.DiskFreePercent),
                PowerEventsEnabled = Flag("powerEventsEnabled", s.PowerEventsEnabled),
                CooldownMinutes = Number("cooldownMinutes", s.CooldownMinutes),
                NtfyEnabled = Flag("ntfyEnabled", s.NtfyEnabled),
                NtfyServer = change.NtfyServer?.TrimEnd('/') ?? s.NtfyServer,
            };
            if (updated.NtfyEnabled && (updated.NtfyTopic is null || Flag("regenerateTopic", false)))
            {
                updated = updated with { NtfyTopic = AlertSettings.NewTopic() };
            }
            if (updated.Validate() is { } problem)
            {
                return problem;
            }
            AlertSettingsStore.Save(_settingsPath, updated);
            _settings = updated;
            return null;
        }
    }

    public JsonObject ToJson()
    {
        var s = Settings;
        return new JsonObject
        {
            ["gpuTemperatureEnabled"] = s.GpuTemperatureEnabled,
            ["gpuTemperatureC"] = s.GpuTemperatureC,
            ["cpuTemperatureEnabled"] = s.CpuTemperatureEnabled,
            ["cpuTemperatureC"] = s.CpuTemperatureC,
            ["cpuUsageEnabled"] = s.CpuUsageEnabled,
            ["cpuUsagePercent"] = s.CpuUsagePercent,
            ["memoryUsageEnabled"] = s.MemoryUsageEnabled,
            ["memoryUsagePercent"] = s.MemoryUsagePercent,
            ["diskFreeEnabled"] = s.DiskFreeEnabled,
            ["diskFreePercent"] = s.DiskFreePercent,
            ["powerEventsEnabled"] = s.PowerEventsEnabled,
            ["cooldownMinutes"] = s.CooldownMinutes,
            ["ntfy"] = new JsonObject
            {
                ["enabled"] = s.NtfyEnabled,
                ["server"] = s.NtfyServer,
                ["topic"] = s.NtfyEnabled ? s.NtfyTopic : null,
            },
            ["recent"] = new JsonArray([.. Recent.All.Take(20).Select(r => (JsonNode)new JsonObject
            {
                ["time"] = r.Time.ToString("O", CultureInfo.InvariantCulture),
                ["title"] = r.Alert.Title,
                ["message"] = r.Alert.Message,
                ["delivered"] = r.Delivered,
            })]),
        };
    }

    private async Task RaiseAsync(AlertEvent? alert, CancellationToken cancellationToken)
    {
        if (alert is null)
        {
            return;
        }
        var delivered = await _notifier.SendAsync(alert, Settings, cancellationToken).ConfigureAwait(false);
        Recent.Add(_time.GetUtcNow(), alert, delivered);
        History?.Record("alert", alert.Title, ok: true, detail: alert.Message);
        LogAlert(_logger, alert.Key, delivered);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert {Key} (delivered: {Delivered})")]
    private static partial void LogAlert(ILogger logger, string key, bool delivered);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert evaluation failed")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception);
}
