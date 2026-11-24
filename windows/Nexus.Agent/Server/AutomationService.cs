using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Core.Automation;
using Nexus.Core.Power;
using Nexus.Core.Protocol;
using Nexus.Core.Security;
using Nexus.Core.Session;

namespace Nexus.Agent.Server;

/// <summary>
/// Runs automations on the PC (P6), with or without a phone connected. Every action is one of the
/// fixed kinds in <see cref="AutomationRules"/>; each run re-checks that the device that created the
/// rule still exists and still holds the permissions its actions need (revoking a phone stops its rules).
/// </summary>
/// <remarks>
/// Owner-only (SEC-5): a rule belongs to the paired device in CreatedBy. Only that device sees, changes,
/// runs or stops it; anyone else gets "not found". A rule whose owner is gone (revoked, never recorded,
/// damaged key) is ownerless: it never runs, does not count towards the limit, and an administrator can
/// give it to a paired device (<see cref="AssignOwnerAsync"/>).
/// <para>
/// Revocation gate: every step that acts on the PC runs inside <c>_gate</c>, after a fresh check of the
/// owner and its permissions; <see cref="ChangeDeviceAsync"/> and <see cref="RevokeDeviceAsync"/> change devices
/// inside the same gate. So a step either started before the change (and the change waits for it) or sees the
/// change and does not start. A revocation also marks the device (<c>_revoking</c>) before it waits for the gate:
/// from then on no rule of that device starts, no waiting step of it begins, and its running rules are cancelled,
/// except the one step already inside the gate (<c>_inFlightRuleId</c>), which the revocation waits for.
/// Lock order: <c>_gate</c> (async, outermost) → <c>_lock</c> → the device store's own lock. Nothing waits for
/// <c>_gate</c> while holding <c>_lock</c>, and "wait" steps never hold <c>_gate</c>.
/// </para>
/// </remarks>
internal sealed partial class AutomationService : BackgroundService
{
    public static readonly TimeSpan PcStartedDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SignInDebounce = TimeSpan.FromMinutes(10);

    private readonly LocalServices _services;
    private readonly ILogger<AutomationService> _logger;
    private readonly string _path;
    private readonly AutomationEngine _engine;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, bool Ok, string Summary)> _lastRun = new(StringComparer.Ordinal);
    // Devices being revoked, with a count for parallel revocations of the same device. Under _lock.
    private readonly Dictionary<Guid, int> _revoking = [];
    // The rule whose step is inside _gate right now (at most one: _gate is SemaphoreSlim(1, 1)). Under _lock.
    private string? _inFlightRuleId;
    // Runs that left _running but have not written their history entry yet (IdleAsync). Under _lock.
    private int _finishing;
    private List<AutomationDefinition> _rules;
    private DateTimeOffset _lastSignIn = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEvaluated;
    private CancellationToken _stopping;

    public AutomationService(LocalServices services, string dataDirectory, TimeZoneInfo timeZone, ILogger<AutomationService> logger)
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
                StartTriggered(rule, "ПК включился");
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
            StartTriggered(rule, rule.Trigger.Kind == TriggerKind.Time ? "по расписанию" : "по датчику");
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
            StartTriggered(rule, "вход в Windows");
        }
    }

    /// <summary>A refusal with its protocol error code.</summary>
    public sealed record Refusal(string Code, string Message);

    private static readonly Refusal NotFound = new(ErrorCodes.AutomationNotFound, "Такой автоматизации нет.");

    /// <summary>
    /// Saves a rule for <paramref name="caller"/>. An existing id that belongs to another device, or to
    /// nobody, is "not found" and stays as it is (no take-over, and its existence is not revealed).
    /// </summary>
    public Refusal? Upsert(AutomationDefinition automation, string? caller)
    {
        ArgumentNullException.ThrowIfNull(automation);
        if (OwnerGuid(caller) is not { } owner)
        {
            return NotFound;
        }
        var rule = automation with { CreatedBy = owner.ToString("D") };
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0 && !IsConfirmedOwner(_rules[index], caller))
            {
                return NotFound;
            }
            // Ownerless rules never run, so they do not take places from rules that do.
            if (index < 0 && _rules.Count(HasConfirmedOwner) >= AutomationRules.MaxAutomations)
            {
                return new Refusal(ErrorCodes.AutomationLimit, $"Не больше {AutomationRules.MaxAutomations} автоматизаций.");
            }
            if (index >= 0)
            {
                _rules[index] = rule;
            }
            else
            {
                _rules.Add(rule);
            }
            Save(_rules);
        }
        if (!rule.Enabled)
        {
            Stop(rule.Id);
        }
        return null;
    }

    /// <returns>False if there is no such rule of <paramref name="caller"/>.</returns>
    public bool Delete(string id, string? caller)
    {
        bool removed;
        lock (_lock)
        {
            removed = _rules.RemoveAll(r => r.Id == id && IsConfirmedOwner(r, caller)) > 0;
            if (removed)
            {
                Save(_rules);
            }
        }
        if (removed)
        {
            Stop(id);
        }
        return removed;
    }

    public AutomationDefinition? Find(string id)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>The rule only if <paramref name="caller"/> is its confirmed owner.</summary>
    public AutomationDefinition? FindOwned(string id, string? caller)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == id && IsConfirmedOwner(r, caller));
        }
    }

    /// <summary>
    /// A rule has a confirmed owner when CreatedBy is a device id (any Guid format) of a device paired with
    /// this installation whose stored key is a valid P-256 key.
    /// </summary>
    public bool HasConfirmedOwner(AutomationDefinition rule) => OwnerOf(rule) is not null;

    /// <summary>Compares ids as Guids (not strings), so case and format do not matter.</summary>
    public bool IsConfirmedOwner(AutomationDefinition rule, string? caller) =>
        OwnerGuid(caller) is { } id && OwnerOf(rule) is { } owner && owner.DeviceId == id;

    private DeviceRecord? OwnerOf(AutomationDefinition rule) =>
        OwnerGuid(rule.CreatedBy) is { } id && _services.Devices.Find(id) is { } device
        && DeviceCrypto.IsValidP256PublicKey(device.PublicKeySpki) ? device : null;

    private static Guid? OwnerGuid(string? id) =>
        id is not null && Guid.TryParse(id, out var guid) && guid != Guid.Empty ? guid : null;

    /// <summary>
    /// Changes a device (revoke, permissions) inside the revocation gate, so no automation step is between
    /// its check and its start while the change happens; then stops the device's running rules that are no
    /// longer allowed. Waits for a step that has already started (at most one tray call).
    /// </summary>
    public async Task<bool> ChangeDeviceAsync(Guid deviceId, Func<bool> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        bool changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            changed = change();
        }
        finally
        {
            _gate.Release();
        }
        if (changed)
        {
            StopNotAllowed(deviceId);
        }
        return changed;
    }

    /// <summary>Stops the device's running rules that are no longer allowed.</summary>
    private void StopNotAllowed(Guid deviceId)
    {
        lock (_lock)
        {
            // Collect first: a cancelled run may finish inline and remove itself from _running.
            var stop = _running
                .Where(r => _rules.FirstOrDefault(x => x.Id == r.Key) is { } rule && OwnerGuid(rule.CreatedBy) == deviceId && OwnerProblem(rule) is not null)
                .Select(r => r.Value)
                .ToList();
            foreach (var cts in stop)
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>What a revocation did.</summary>
    /// <param name="Changed">The device existed and was removed.</param>
    /// <param name="HasPowerRules">
    /// The device has a rule with a shutdown or restart step. This does not mean that a shutdown is scheduled.
    /// </param>
    public sealed record RevokeOutcome(bool Changed, bool HasPowerRules);

    /// <summary>For a revoked device with shutdown or restart rules. Conditional: nothing is claimed to be scheduled.</summary>
    public const string PowerRulesWarning =
        "У этого устройства есть автоматизации с выключением или перезагрузкой. Если одна из них уже запланировала выключение, отзыв его не отменяет.";

    /// <summary>
    /// Revokes a device. An unknown device changes nothing (no cancellation, no mark). Otherwise the device is
    /// marked first (no new runs, no waiting step of it begins), its running rules are cancelled except the step
    /// already inside the gate, and <paramref name="remove"/> runs inside the gate once that step has finished.
    /// The mark is removed afterwards whatever happens (false, exception, cancellation).
    /// </summary>
    public async Task<RevokeOutcome> RevokeDeviceAsync(Guid deviceId, Func<bool> remove, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remove);
        if (_services.Devices.Find(deviceId) is null)
        {
            return new RevokeOutcome(false, false);
        }
        bool hasPowerRules;
        lock (_lock)
        {
            _revoking[deviceId] = _revoking.GetValueOrDefault(deviceId) + 1;
            // From the stored rules, whether or not they ever ran.
            hasPowerRules = _rules.Any(r => OwnerGuid(r.CreatedBy) == deviceId && AutomationRules.NeedsConfirmation(r));
            // Collect first: a cancelled run may finish inline and remove itself from _running. The CTS stays
            // RunAsync's to dispose (it removes itself from _running under this lock before it does).
            var stop = _running
                .Where(r => r.Key != _inFlightRuleId && _rules.FirstOrDefault(x => x.Id == r.Key) is { } rule && OwnerGuid(rule.CreatedBy) == deviceId)
                .Select(r => r.Value)
                .ToList();
            foreach (var cts in stop)
            {
                cts.Cancel();
            }
        }
        try
        {
            bool changed;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                changed = remove();
            }
            finally
            {
                _gate.Release();
            }
            if (changed)
            {
                StopNotAllowed(deviceId);
                if (hasPowerRules)
                {
                    _services.History.Record("automation", "Устройство отозвано: у него есть автоматизации с выключением или перезагрузкой", true,
                        "Если одна из них уже запланировала выключение или перезагрузку, отзыв его не отменяет.");
                }
            }
            return new RevokeOutcome(changed, hasPowerRules);
        }
        finally
        {
            lock (_lock)
            {
                if (_revoking.TryGetValue(deviceId, out var count) && count > 1)
                {
                    _revoking[deviceId] = count - 1;
                }
                else
                {
                    _revoking.Remove(deviceId);
                }
            }
        }
    }

    /// <summary>True while a revocation of <paramref name="deviceId"/> is in progress (tests).</summary>
    public bool IsRevoking(Guid deviceId)
    {
        lock (_lock)
        {
            return _revoking.ContainsKey(deviceId);
        }
    }

    /// <summary>The rule whose step is inside the revocation gate right now, if any (tests).</summary>
    public string? InFlightRuleId
    {
        get
        {
            lock (_lock)
            {
                return _inFlightRuleId;
            }
        }
    }

    private bool IsRevokingOwner(AutomationDefinition rule)
    {
        lock (_lock)
        {
            return OwnerGuid(rule.CreatedBy) is { } owner && _revoking.ContainsKey(owner);
        }
    }

    /// <summary>Rules without a confirmed owner (administrator: "automations owners").</summary>
    public JsonArray Ownerless()
    {
        lock (_lock)
        {
            return new JsonArray([.. _rules.Where(r => !HasConfirmedOwner(r)).Select(r => (JsonNode)new JsonObject
            {
                ["automationId"] = r.Id,
                ["name"] = r.Name,
                ["formerOwner"] = r.CreatedBy,
                ["requiredPermissions"] = new JsonArray([.. Permissions.ToWireNames(AutomationRules.RequiredFor(r)).Select(p => (JsonNode)p)]),
            })]);
        }
    }

    /// <summary>
    /// Gives an ownerless rule to a device paired with this installation. The administrator types the
    /// device's key fingerprint (from "devices"), so an id alone is not enough. The device must hold every
    /// permission the rule needs. Nothing changes on any failure. Runs inside the revocation gate, so the
    /// device cannot be revoked between the checks and the assignment. A device whose revocation has already
    /// started (marked, still waiting for the gate) is refused with "device is being revoked".
    /// </summary>
    /// <returns>Null on success, otherwise why not.</returns>
    public async Task<string?> AssignOwnerAsync(string automationId, Guid deviceId, string? fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(automationId);
        // Every attempt is audited, whatever ends it (refusal, failed save, cancellation): the default is a failure.
        string? error = "not completed";
        string? name = null;
        string? formerOwner = null;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_lock)
                {
                    error = Assign(automationId, deviceId, fingerprint, out name, out formerOwner);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _services.Audit.RecordAutomationAssign(deviceId.ToString("D"), automationId, formerOwner, error is null, error);
        }
        if (error is null)
        {
            _services.History.Record("automation", $"Автоматизация «{name}»: назначен владелец", true, _services.Devices.Find(deviceId)?.Name);
        }
        return error;
    }

    private string? Assign(string automationId, Guid deviceId, string? fingerprint, out string? name, out string? formerOwner)
    {
        name = null;
        formerOwner = null;
        var index = _rules.FindIndex(r => r.Id == automationId);
        if (index < 0)
        {
            return "automation not found";
        }
        var rule = _rules[index];
        formerOwner = rule.CreatedBy;
        if (HasConfirmedOwner(rule))
        {
            return "automation already has an owner";
        }
        if (_services.Devices.Find(deviceId) is not { } device || !DeviceCrypto.IsValidP256PublicKey(device.PublicKeySpki))
        {
            return "device is not paired with this PC";
        }
        if (_revoking.ContainsKey(deviceId))
        {
            return "device is being revoked";
        }
        if (!DeviceCrypto.DeviceKeyFingerprintMatches(device.PublicKeySpki, fingerprint))
        {
            return "fingerprint does not match the device";
        }
        var required = AutomationRules.RequiredFor(rule);
        if ((device.Permissions & required) != required)
        {
            return "device lacks permissions: " + string.Join(", ", Permissions.ToWireNames(required & ~device.Permissions));
        }
        if (_rules.Count(HasConfirmedOwner) >= AutomationRules.MaxAutomations)
        {
            return $"limit of {AutomationRules.MaxAutomations} automations reached";
        }
        var updated = new List<AutomationDefinition>(_rules);
        updated[index] = rule with { CreatedBy = deviceId.ToString("D") };
        try
        {
            Save(updated); // first on disk: if this fails, the rule stays as it was
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "could not save automations.json: " + ex.Message;
        }
        _rules = updated;
        name = rule.Name;
        return null;
    }

    /// <returns>False if it is already running, or its owner is being revoked.</returns>
    public bool Start(AutomationDefinition rule, string reason) => TryStart(rule, reason) == StartResult.Started;

    private enum StartResult
    {
        Started,
        Running,
        Revoking,
    }

    private StartResult TryStart(AutomationDefinition rule, string reason)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        lock (_lock)
        {
            var refused = IsRevokingOwner(rule) ? StartResult.Revoking
                : !_running.TryAdd(rule.Id, cts) ? StartResult.Running
                : StartResult.Started;
            if (refused != StartResult.Started)
            {
                cts.Dispose();
                return refused;
            }
        }
        _ = Task.Run(() => RunAsync(rule, reason, cts), CancellationToken.None);
        return StartResult.Started;
    }

    /// <summary>
    /// A trigger fired: rules without a confirmed owner do not run; history notes it once per firing
    /// (the engine fires a rule once per time, event or threshold crossing, not on every tick).
    /// </summary>
    private void StartTriggered(AutomationDefinition rule, string reason)
    {
        if (MissingPlanFeature(rule) is { } feature)
        {
            // ENT-2: after Pro ends the rule stays, but triggers skip it (COMMERCIAL_PLAN section 0).
            lock (_lock)
            {
                _lastRun[rule.Id] = (_services.Time.GetUtcNow(), false, "пропущена: нужен Nexus PC Pro");
            }
            _services.History.Record("automation", $"Автоматизация «{rule.Name}»: пропущена", false,
                $"{reason}: для этой автоматизации нужен Nexus PC Pro ({feature})");
            return;
        }
        if (HasConfirmedOwner(rule))
        {
            // The revocation is checked under _lock together with the start (TryStart).
            if (TryStart(rule, reason) == StartResult.Revoking)
            {
                lock (_lock)
                {
                    _lastRun[rule.Id] = (_services.Time.GetUtcNow(), false, "пропущена: устройство отзывается");
                }
                _services.History.Record("automation", $"Автоматизация «{rule.Name}»: пропущена", false,
                    $"Пропущена: устройство отзывается ({reason})");
            }
            return;
        }
        const string summary = "пропущена: у автоматизации нет владельца";
        lock (_lock)
        {
            _lastRun[rule.Id] = (_services.Time.GetUtcNow(), false, summary);
        }
        _services.History.Record("automation", $"Автоматизация «{rule.Name}»: пропущена", false,
            $"{reason}: устройство, создавшее автоматизацию, больше не сопряжено с этим ПК");
    }

    /// <summary>First plan feature the rule needs and the PC does not have now; null if it may run (or plans are not enforced).</summary>
    internal string? MissingPlanFeature(AutomationDefinition rule)
    {
        // Without the PC key (degraded) this is always the Free plan, never "not enforced".
        if (_services.EnforcedPlan is not { } status)
        {
            return null;
        }
        return Nexus.Core.Entitlements.PlanPolicy.RequiredFor(rule).FirstOrDefault(f => !status.Has(f));
    }

    /// <returns>Null if there is no such rule of <paramref name="caller"/>; otherwise whether it was running.</returns>
    public bool? Stop(string id, string? caller)
    {
        lock (_lock)
        {
            if (!_rules.Any(r => r.Id == id && IsConfirmedOwner(r, caller)))
            {
                return null;
            }
            return Stop(id);
        }
    }

    private bool Stop(string id)
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

    /// <summary>Waits until no automation is running and every finished run is in history (tests).</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            lock (_lock)
            {
                if (_running.Count == 0 && _finishing == 0)
                {
                    return;
                }
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>The rules of <paramref name="caller"/> only.</summary>
    public JsonObject ToJson(string? caller)
    {
        var list = new JsonArray();
        lock (_lock)
        {
            foreach (var rule in _rules.Where(r => IsConfirmedOwner(r, caller)))
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
                string? error;
                if (action.Kind == "wait")
                {
                    error = await ExecuteAsync(rule, action, cts.Token).ConfigureAwait(false); // never inside the gate
                }
                else
                {
                    // Check and start of the step inside the gate: a revocation is either before (seen here) or after.
                    await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
                    string? problem;
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        lock (_lock)
                        {
                            // Check and mark together: a revocation either sees this step in flight or the step sees it.
                            problem = OwnerProblem(rule);
                            if (problem is null)
                            {
                                _inFlightRuleId = rule.Id;
                            }
                        }
                        try
                        {
                            error = problem is null ? await ExecuteAsync(rule, action, cts.Token).ConfigureAwait(false) : null;
                        }
                        finally
                        {
                            lock (_lock)
                            {
                                if (_inFlightRuleId == rule.Id)
                                {
                                    _inFlightRuleId = null;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                    if (problem is not null)
                    {
                        failures.Add(problem);
                        break;
                    }
                }
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
                _finishing++; // until its history entry is written (IdleAsync)
            }
            cts.Dispose();
        }

        var ok = failures.Count == 0 && !stopped;
        var summary = stopped ? $"остановлена ({done} из {rule.Actions.Count})"
            : ok ? $"выполнено ({reason})"
            : $"с ошибками ({reason}): {string.Join("; ", failures)}";
        try
        {
            lock (_lock)
            {
                _lastRun[rule.Id] = (_services.Time.GetUtcNow(), ok, summary);
            }
            _services.History.Record("automation", $"Автоматизация «{rule.Name}»: {(stopped ? "остановлена" : ok ? "выполнена" : "с ошибками")}", ok, summary);
        }
        finally
        {
            lock (_lock)
            {
                _finishing--;
            }
        }
    }

    /// <summary>
    /// Fresh check before each step: the owner is still paired and still holds every permission the
    /// rule needs (as when it was saved), not just this step's; and no revocation of it has begun.
    /// </summary>
    private string? OwnerProblem(AutomationDefinition rule)
    {
        var required = AutomationRules.RequiredFor(rule);
        if (OwnerOf(rule) is not { } device)
        {
            return "устройство, создавшее автоматизацию, отозвано";
        }
        lock (_lock)
        {
            if (_revoking.ContainsKey(device.DeviceId))
            {
                return "устройство, создавшее автоматизацию, отзывается";
            }
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
        OwnerOf(rule) is { } device && device.Permissions.HasFlag(permission);

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
        _ => action.Kind,
    };

    private void Save(IEnumerable<AutomationDefinition> rules)
    {
        var json = new JsonArray([.. rules.Select(r => (JsonNode)AutomationJson.ToJson(r))]);
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
            foreach (var stored in doc.RootElement.EnumerateArray())
            {
                // Notifications (ntfy) were removed: drop their steps from rules saved by older versions.
                using var item = AutomationJson.WithoutLegacySteps(stored);
                if (item is not null && AutomationJson.Parse(item.RootElement, null, out var rule, out _))
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
