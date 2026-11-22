using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Power;
using Nexus.Windows;
using Nexus.Core.Pairing;
using Nexus.Core.Session;
using Nexus.Core.Protocol;
using Nexus.Core.Security;
using Nexus.Agent.Storage;

namespace Nexus.Agent.Server;

/// <summary>Validates every request through <see cref="CommandValidator"/>, then runs its handler.</summary>
internal sealed class CommandDispatcher(AgentServices services)
{
    private readonly InputAuditThrottle _inputAudit = new(services.Time);

    /// <summary>Commands the agent handles itself.</summary>
    public static readonly IReadOnlyList<string> Implemented =
    [
        "system.hello", "pair.request", "auth.begin", "auth.prove", "pc.status",
        "power.shutdown", "power.restart", "power.sleep", "power.cancel",
        "metrics.snapshot", "metrics.history",
        "rgb.status", "rgb.apply", "rgb.profiles.upsert", "rgb.profiles.delete", "rgb.profiles.apply",
        "automations.list", "automations.upsert", "automations.delete", "automations.run", "automations.stop", "history.list",
    ];

    /// <summary>Commands that need Nexus.Tray in the signed-in session; available only while it is connected.</summary>
    public static readonly IReadOnlyList<string> ViaTray =
    [
        "power.lock", "apps.list", "apps.launch", "apps.close",
        "profiles.list", "profiles.run", "profiles.upsert", "profiles.delete",
        "input.move", "input.moveTo", "input.button", "input.scroll", "input.text", "input.key", "input.media",
        "screen.start", "screen.frame", "screen.stop", "screen.video",
        "files.list", "files.upload.begin", "files.upload.chunk", "files.upload.finish", "files.upload.cancel",
        "files.read", "files.hash", "clipboard.get", "clipboard.set",
    ];

    /// <summary>Sent many times a second (touchpad, file chunks): successful ones are audited at most once a minute.</summary>
    private static bool IsHighFrequency(string type) =>
        type.StartsWith("input.", StringComparison.Ordinal) || type is "files.upload.chunk" or "files.read" or "screen.frame" or "screen.video";

    /// <summary>In the protocol but without a handler in this build: answered with command.notImplemented, never listed in capabilities.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NotYet = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // ENT-1 adds the command to protocol v1; the agent handles it in ENT-2.
        ["account.refresh"] = "'account.refresh' is not available in this build yet.",
    };

    /// <summary>
    /// Minimum countdown for a shutdown/restart without force when it has to go through the service,
    /// which must force-close other sessions (see WindowsPowerController). With Nexus.Tray connected,
    /// non-forced requests run in the user's session instead and apps can ask to save.
    /// </summary>
    public const int MinGraceSeconds = 30;

    // Lets a sleep/immediate shutdown response reach the phone before the PC goes down.
    private static readonly TimeSpan AfterResponseDelay = TimeSpan.FromSeconds(1);

    public async Task<HandlerResult> HandleAsync(string json, ConnectionSession session, CancellationToken cancellationToken)
    {
        var client = session.CurrentClient();
        var validation = services.Validator.Validate(json, client);
        if (validation.Command is not { } command)
        {
            var error = validation.Error!;
            services.Audit.Record(client.DeviceId, TryReadType(json) ?? "?", ok: false, error.Code, session.Remote);
            return Fail(TryReadId(json), error.Code, error.Message);
        }

        HandlerResult result;
        try
        {
            result = await RunAsync(command, session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A handler bug must produce an error response, not kill the connection.
        catch (Exception)
#pragma warning restore CA1031
        {
            result = Fail(command.Envelope.Id, ErrorCodes.Internal, "Internal error.");
        }

        var deviceId = session.CurrentClient().DeviceId ?? client.DeviceId;
        if (!result.Ok || !IsHighFrequency(command.Spec.Type) || _inputAudit.ShouldRecord(deviceId, command.Spec.Type))
        {
            // A touchpad sends dozens of events a second: successful input is recorded at most once a minute
            // per device, so it cannot push older entries out of the rotating log. Failures are always recorded.
            services.Audit.Record(deviceId, command.Spec.Type, result.Ok, result.ErrorCode, session.Remote);
        }
        RecordHistory(command, result, deviceId);
        return result;
    }

    private async Task<HandlerResult> RunAsync(ValidatedCommand command, ConnectionSession session, CancellationToken cancellationToken)
    {
        var id = command.Envelope.Id;
        if (NotYet.TryGetValue(command.Spec.Type, out var reason))
        {
            return Fail(id, ErrorCodes.NotImplemented, reason);
        }

        if (ViaTray.Contains(command.Spec.Type))
        {
            var forwarded = await ForwardToTrayAsync(id, command, session, cancellationToken).ConfigureAwait(false);
            // Linked lighting only for devices that also hold the rgb permission.
            if (forwarded.Ok && command.Payload is ProfileRefPayload { } run && command.Spec.Type == "profiles.run"
                && session.CurrentClient().Permissions.HasFlag(Permission.Rgb))
            {
                return await WithLightingAsync(id, forwarded, run.ProfileId, cancellationToken).ConfigureAwait(false);
            }
            return forwarded;
        }
        if (command.Spec.Type.StartsWith("automations.", StringComparison.Ordinal) && services.Automations is { } automations)
        {
            return Automations(id, command, session, automations);
        }
        if (command.Payload is Nexus.Core.Automation.HistoryListPayload historyList)
        {
            return Ok(id, new JsonObject
            {
                ["entries"] = new JsonArray([.. services.History.List(historyList.Limit, historyList.Before).Select(e => (JsonNode)HistoryStore.ToJson(e))]),
            });
        }
        if (command.Spec.Type.StartsWith("rgb.", StringComparison.Ordinal) && services.Rgb is { } rgb)
        {
            return await RgbAsync(id, command, rgb, cancellationToken).ConfigureAwait(false);
        }

        switch (command.Payload)
        {
            case PairRequestPayload pair:
                return await PairAsync(id, pair, session, cancellationToken).ConfigureAwait(false);
            case AuthBeginPayload begin:
                return AuthBegin(id, begin, session);
            case AuthProvePayload prove:
                return AuthProve(id, prove, session);
            case MetricsHistoryPayload history:
                var (resolution, points) = services.Metrics.History(history.Range);
                return Ok(id, MetricsJson.History(history.Range, resolution, points));
        }

        return command.Spec.Type switch
        {
            "system.hello" => Ok(id, Hello(session)),
            "pc.status" => Ok(id, Status()),
            "metrics.snapshot" => Ok(id, MetricsJson.Snapshot(services.Metrics.Latest ?? services.Metrics.SampleNow())),
            "power.shutdown" or "power.restart" => await PowerAsync(id, command, session, cancellationToken).ConfigureAwait(false),
            "power.sleep" => Sleep(id, session),
            "power.cancel" => FromOutcome(id, services.Power.Cancel(), new JsonObject { ["cancelled"] = true }),
            _ => Fail(id, ErrorCodes.NotImplemented, $"'{command.Spec.Type}' has no handler."),
        };
    }

    private JsonObject Hello(ConnectionSession session) => new()
    {
        ["protocol"] = ProtocolInfo.Version,
        ["pcId"] = services.Identity.PcId.ToString("D"),
        ["pcName"] = services.Identity.PcName,
        ["agentVersion"] = services.Identity.Version,
        ["authenticated"] = session.CurrentClient().IsAuthenticated,
        // Exactly what works right now: desktop commands only while Nexus.Tray is connected.
        ["capabilities"] = new JsonArray([.. Implemented.Concat(services.Trays.IsAvailable ? ViaTray : []).Select(t => (JsonNode)t)]),
    };

    private JsonObject Status() => new()
    {
        ["pcId"] = services.Identity.PcId.ToString("D"),
        ["pcName"] = services.Identity.PcName,
        ["os"] = RuntimeInformation.OSDescription,
        ["uptimeSeconds"] = Environment.TickCount64 / 1000,
        ["agentVersion"] = services.Identity.Version,
        // Whether Nexus.Tray runs in the signed-in session (needed for apps, profiles, lock).
        ["userSession"] = services.Trays.IsAvailable ? "available" : "unavailable",
        // Current addresses (Wi-Fi or the LAN address may change) and Wake-on-LAN targets, so the
        // phone can reach and wake the PC later. Only for authenticated devices.
        ["hosts"] = new JsonArray([.. Hosting.NetworkAddresses.EligibleLocalAddresses().Select(a => (JsonNode)a.ToString())]),
        ["wake"] = new JsonArray([.. Hosting.NetworkAddresses.WakeTargets().Select(t => (JsonNode)new JsonObject
        {
            ["interface"] = t.Interface,
            ["mac"] = t.Mac,
            ["broadcast"] = t.Broadcast,
            ["wireless"] = t.Wireless,
        })]),
    };

    private async Task<HandlerResult> PairAsync(Guid id, PairRequestPayload pair, ConnectionSession session, CancellationToken cancellationToken)
    {
        if (session.CurrentClient().IsAuthenticated)
        {
            return Fail(id, ErrorCodes.PairBusy, "This connection is already authenticated.");
        }
        var result = await services.Pairing.SubmitAsync(pair.Code, pair.DeviceName, pair.PublicKeySpki, services.Identity.Fingerprint, cancellationToken).ConfigureAwait(false);
        if (result is { Status: PairingStatus.Approved, Device: { } device })
        {
            session.ResetHandshakeDeadline();
            return Ok(id, new JsonObject
            {
                ["deviceId"] = device.DeviceId.ToString("D"),
                ["pcId"] = services.Identity.PcId.ToString("D"),
                ["permissions"] = new JsonArray([.. Permissions.ToWireNames(device.Permissions).Select(p => (JsonNode)p)]),
            });
        }
        return result.Status switch
        {
            PairingStatus.NoActiveSession => Fail(id, ErrorCodes.PairNoActiveSession, "Pairing is not open on the PC."),
            PairingStatus.InvalidCode => Fail(id, ErrorCodes.PairInvalidCode, "Wrong pairing code."),
            PairingStatus.Busy => Fail(id, ErrorCodes.PairBusy, "Another device is waiting for approval."),
            PairingStatus.Rejected => Fail(id, ErrorCodes.PairRejected, "Pairing was rejected on the PC."),
            PairingStatus.DeviceLimitReached => Fail(id, ErrorCodes.PairDeviceLimit, "Too many paired devices."),
            _ => Fail(id, ErrorCodes.PairExpired, "Pairing expired."),
        };
    }

    private static HandlerResult AuthBegin(Guid id, AuthBeginPayload begin, ConnectionSession session)
    {
        // Same answer whether or not the device exists: no device enumeration.
        var nonce = RandomNumberGenerator.GetBytes(DeviceCrypto.NonceBytes);
        session.PendingChallenge = (begin.DeviceId, nonce);
        return Ok(id, new JsonObject { ["nonce"] = Convert.ToBase64String(nonce) });
    }

    private HandlerResult AuthProve(Guid id, AuthProvePayload prove, ConnectionSession session)
    {
        if (session.PendingChallenge is not { } challenge)
        {
            session.AuthFailures++;
            return Fail(id, ErrorCodes.AuthFailed, "Call auth.begin first.");
        }
        session.PendingChallenge = null; // a nonce is good for exactly one attempt

        var device = services.Devices.Find(challenge.DeviceId);
        var transcript = DeviceCrypto.AuthTranscript(services.Identity.PcId, challenge.DeviceId, challenge.Nonce, services.Identity.Fingerprint);
        if (device is null || !DeviceCrypto.VerifySignature(device.PublicKeySpki, transcript, prove.Signature))
        {
            session.AuthFailures++;
            return Fail(id, ErrorCodes.AuthFailed, "Authentication failed.");
        }

        session.MarkAuthenticated(device.DeviceId);
        return Ok(id, new JsonObject
        {
            ["deviceId"] = device.DeviceId.ToString("D"),
            ["permissions"] = new JsonArray([.. Permissions.ToWireNames(device.Permissions).Select(p => (JsonNode)p)]),
            ["expiresAt"] = (session.AuthenticatedAt + ConnectionSession.AuthLifetime).ToString("O"),
        });
    }

    private static async Task<HandlerResult> RgbAsync(Guid id, ValidatedCommand command, RgbService rgb, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.Payload)
            {
                case RgbApplyPayload apply:
                    return Ok(id, await rgb.ApplyAsync(apply.Look, apply.Devices, cancellationToken).ConfigureAwait(false));
                case RgbProfileUpsertPayload upsert:
                    rgb.Upsert(upsert.Profile);
                    return Ok(id, new JsonObject { ["profiles"] = rgb.ProfilesJson() });
                case ProfileRefPayload profile when command.Spec.Type == "rgb.profiles.delete":
                    return rgb.Delete(profile.ProfileId)
                        ? Ok(id, new JsonObject { ["profiles"] = rgb.ProfilesJson() })
                        : Fail(id, ErrorCodes.ProfileNotFound, "Такого профиля подсветки нет.");
                case ProfileRefPayload profile:
                    return rgb.Find(profile.ProfileId) is { } found
                        ? Ok(id, await rgb.ApplyAsync(found.Look, null, cancellationToken).ConfigureAwait(false))
                        : Fail(id, ErrorCodes.ProfileNotFound, "Такого профиля подсветки нет.");
                default:
                    return Ok(id, await rgb.StatusAsync(cancellationToken).ConfigureAwait(false));
            }
        }
        catch (RgbService.RgbUnavailableException ex)
        {
            return Fail(id, ErrorCodes.RgbUnavailable, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(id, ErrorCodes.ProfileLimit, ex.Message);
        }
    }

    /// <summary>After an app profile runs, applies the lighting profile with the same id, if any. Never fails the run.</summary>
    private async Task<HandlerResult> WithLightingAsync(Guid id, HandlerResult forwarded, string profileId, CancellationToken cancellationToken)
    {
        if (services.Rgb?.Find(profileId) is not { } lighting)
        {
            return forwarded;
        }
        string status;
        try
        {
            var result = await services.Rgb.ApplyAsync(lighting.Look, null, cancellationToken).ConfigureAwait(false);
            status = result["applied"]!.GetValue<int>() > 0 ? "applied" : "noDevices";
        }
        catch (RgbService.RgbUnavailableException)
        {
            status = "unavailable";
        }
        var payload = JsonNode.Parse(forwarded.Response)!["payload"]!.AsObject().DeepClone().AsObject();
        payload["lighting"] = status;
        return Ok(id, payload);
    }

    private async Task<HandlerResult> ForwardToTrayAsync(Guid id, ValidatedCommand command, ConnectionSession session, CancellationToken cancellationToken)
    {
        // The payload was validated against the schema; the Tray re-checks ids against its own list.
        var args = JsonNode.Parse(command.Envelope.Payload.GetRawText()) as JsonObject ?? [];
        if (command.Spec.Type == "screen.start" && session.AuthenticatedDevice is { } deviceId)
        {
            // Who is asking, from the authenticated session (the schema allows no fields from the phone here).
            args["deviceId"] = deviceId.ToString("D");
            args["deviceName"] = services.Devices.Find(deviceId)?.Name ?? "Устройство";
        }
        // Hashing a large file can take a while; screen.start may wait for the person at the PC to answer.
        var timeout = command.Spec.Type switch
        {
            "files.upload.finish" or "files.hash" => TimeSpan.FromMinutes(5),
            "screen.start" => TimeSpan.FromMinutes(2),
            _ => Nexus.Agent.Session.TrayRegistry.CallTimeout,
        };
        var result = await services.Trays.CallAsync(command.Spec.Type, args, cancellationToken, timeout).ConfigureAwait(false);
        return result.Ok
            ? Ok(id, result.Result ?? [])
            : Fail(id, result.ErrorCode ?? ErrorCodes.Internal, result.Message ?? "Failed.");
    }

    private async Task<HandlerResult> PowerAsync(Guid id, ValidatedCommand command, ConnectionSession session, CancellationToken cancellationToken)
    {
        var payload = (PowerPayload)command.Payload;
        var action = command.Spec.Type == "power.restart" ? PowerAction.Restart : PowerAction.Shutdown;
        var verb = action == PowerAction.Restart ? "restart" : "shutdown";
        var device = session.AuthenticatedDevice is { } deviceId ? services.Devices.Find(deviceId) : null;
        var reason = $"Nexus PC: {verb} requested from {device?.Name ?? "a paired device"}.";

        // Preferred: from the signed-in session via Nexus.Tray, so apps can ask to save first.
        if (!payload.Force && services.Trays.IsAvailable)
        {
            var viaTray = await services.Trays.CallAsync(SessionOps.PowerSchedule, new JsonObject
            {
                ["action"] = verb,
                ["delaySeconds"] = payload.DelaySeconds,
                ["force"] = false,
                ["reason"] = reason,
            }, cancellationToken).ConfigureAwait(false);
            if (viaTray.Ok)
            {
                return Ok(id, PowerResponse(verb, payload.DelaySeconds, "userSession"));
            }
            // e.g. several people signed in: fall back to the service path below.
        }

        // Service path: must force other sessions, so a visible countdown protects unsaved work.
        var delaySeconds = payload.Force ? payload.DelaySeconds : Math.Max(payload.DelaySeconds, MinGraceSeconds);
        var response = PowerResponse(verb, delaySeconds, "service");
        if (delaySeconds > 0)
        {
            return FromOutcome(id, services.Power.Schedule(action, TimeSpan.FromSeconds(delaySeconds), payload.Force, reason), response);
        }
        // Immediate (force only): respond first, because the PC may stop answering a moment later.
        return Deferred(id, response, session, command.Spec.Type, () => services.Power.Schedule(action, TimeSpan.Zero, payload.Force, reason));
    }

    /// <summary>
    /// Owner-only (SEC-5): every command acts only on the caller's own rules. Another device's rule, or an
    /// ownerless one, is answered exactly like a missing one, before any other check, so nothing about it leaks.
    /// </summary>
    private static HandlerResult Automations(Guid id, ValidatedCommand command, ConnectionSession session, AutomationService automations)
    {
        var client = session.CurrentClient();
        switch (command.Payload)
        {
            case Nexus.Core.Automation.AutomationUpsertPayload upsert:
                var required = Nexus.Core.Automation.AutomationRules.RequiredFor(upsert.Automation);
                if ((client.Permissions & required) != required)
                {
                    // No escalation: a rule can only do what its creator may do directly.
                    return MissingPermissions(id, required, client.Permissions);
                }
                if (Nexus.Core.Automation.AutomationRules.NeedsConfirmation(upsert.Automation) && !command.Envelope.Confirmed)
                {
                    return Fail(id, ErrorCodes.ConfirmationRequired, "Автоматизация выключает или перезагружает ПК: подтвердите сохранение.");
                }
                return automations.Upsert(upsert.Automation, client.DeviceId) is { } refused
                    ? Fail(id, refused.Code, refused.Message)
                    : Ok(id, automations.ToJson(client.DeviceId));
            case Nexus.Core.Automation.AutomationRefPayload reference when command.Spec.Type == "automations.delete":
                return automations.Delete(reference.AutomationId, client.DeviceId)
                    ? Ok(id, automations.ToJson(client.DeviceId))
                    : Fail(id, ErrorCodes.AutomationNotFound, "Такой автоматизации нет.");
            case Nexus.Core.Automation.AutomationRefPayload reference when command.Spec.Type == "automations.run":
                if (automations.FindOwned(reference.AutomationId, client.DeviceId) is not { } found)
                {
                    return Fail(id, ErrorCodes.AutomationNotFound, "Такой автоматизации нет.");
                }
                // A manual run is a direct command of the caller: its own permissions and confirmation, as for upsert.
                var needed = Nexus.Core.Automation.AutomationRules.RequiredFor(found);
                if ((client.Permissions & needed) != needed)
                {
                    return MissingPermissions(id, needed, client.Permissions);
                }
                if (Nexus.Core.Automation.AutomationRules.NeedsConfirmation(found) && !command.Envelope.Confirmed)
                {
                    return Fail(id, ErrorCodes.ConfirmationRequired, "Автоматизация выключает или перезагружает ПК: подтвердите запуск.");
                }
                return automations.Start(found, "вручную с телефона")
                    ? Ok(id, new JsonObject { ["started"] = true })
                    : Fail(id, ErrorCodes.AutomationBusy, "Эта автоматизация уже выполняется.");
            case Nexus.Core.Automation.AutomationRefPayload reference:
                return automations.Stop(reference.AutomationId, client.DeviceId) is { } stopped
                    ? Ok(id, new JsonObject { ["stopped"] = stopped })
                    : Fail(id, ErrorCodes.AutomationNotFound, "Такой автоматизации нет.");
            default:
                return Ok(id, automations.ToJson(client.DeviceId));
        }
    }

    private static HandlerResult MissingPermissions(Guid id, Permission required, Permission held)
    {
        var missing = string.Join(", ", Permissions.ToWireNames(required & ~held));
        return Fail(id, ErrorCodes.PermissionDenied, $"Для этих действий у устройства нет разрешения: {missing}.");
    }

    private readonly InputAuditThrottle _historyThrottle = new(services.Time, TimeSpan.FromMinutes(10));

    /// <summary>PC history (P6): commands that change something, in words, without payload contents.</summary>
    private void RecordHistory(ValidatedCommand command, HandlerResult result, string? deviceId)
    {
        var type = command.Spec.Type;
        if (command.Spec.Risk == RiskClass.Read || type is "auth.begin" or "auth.prove" or "files.upload.begin"
            or "files.upload.chunk" or "files.upload.cancel")
        {
            if (type != "files.hash")
            {
                return;
            }
        }
        if (type.StartsWith("input.", StringComparison.Ordinal) && (!result.Ok || !_historyThrottle.ShouldRecord(deviceId, "input")))
        {
            return;
        }
        if (type == "files.hash" && !result.Ok)
        {
            return;
        }
        var payload = command.Envelope.Payload;
        string? Arg(string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Num(string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
        var title = type switch
        {
            "pair.request" => "Сопряжено новое устройство",
            "power.shutdown" => $"Выключение ПК через {FormatDelay(Num("delaySeconds") ?? 0)}",
            "power.restart" => $"Перезагрузка ПК через {FormatDelay(Num("delaySeconds") ?? 0)}",
            "power.sleep" => "Сон",
            "power.lock" => "Блокировка экрана",
            "power.cancel" => "Отмена выключения",
            "apps.launch" => $"Запуск программы «{Arg("appId")}»",
            "apps.close" => $"Закрытие программы «{Arg("appId")}»",
            "profiles.run" => $"Профиль «{Arg("profileId")}»",
            "profiles.upsert" => "Профиль программ изменён",
            "profiles.delete" => $"Профиль «{Arg("profileId")}» удалён",
            "rgb.apply" => "Подсветка изменена",
            "rgb.profiles.apply" => $"Профиль подсветки «{Arg("profileId")}»",
            "rgb.profiles.upsert" => "Профиль подсветки сохранён",
            "rgb.profiles.delete" => $"Профиль подсветки «{Arg("profileId")}» удалён",
            "files.upload.finish" => "Файл получен с телефона",
            "files.hash" => "Файл отправлен на телефон",
            "clipboard.get" => "Телефон прочитал буфер обмена ПК",
            "clipboard.set" => "Телефон положил текст в буфер обмена ПК",
            "screen.start" => "Телефон начал показ экрана ПК",
            "screen.stop" => "Показ экрана завершён",
            "automations.upsert" => "Автоматизация сохранена",
            "automations.delete" => $"Автоматизация «{Arg("automationId")}» удалена",
            "automations.run" => $"Автоматизация «{Arg("automationId")}» запущена вручную",
            "automations.stop" => $"Автоматизация «{Arg("automationId")}» остановлена",
            _ when type.StartsWith("input.", StringComparison.Ordinal) => "Управление мышью и клавиатурой с телефона",
            _ => null,
        };
        if (title is null || (type == "pair.request" && !result.Ok))
        {
            return;
        }
        string? device = null;
        if (Guid.TryParse(deviceId, out var guid))
        {
            device = services.Devices.Find(guid)?.Name;
        }
        services.History.Record("command", title, result.Ok, result.Ok ? null : ResultMessage(result), device);
    }

    private static string FormatDelay(int seconds) => seconds switch
    {
        < 60 => $"{seconds} с",
        < 3600 => $"{seconds / 60} мин",
        _ => $"{seconds / 3600} ч {seconds % 3600 / 60} мин",
    };

    private static string? ResultMessage(HandlerResult result)
    {
        try
        {
            return JsonNode.Parse(result.Response)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private JsonObject PowerResponse(string verb, int delaySeconds, string via) => new()
    {
        ["action"] = verb,
        ["scheduledAt"] = services.Time.GetUtcNow().AddSeconds(delaySeconds).ToString("O"),
        ["delaySeconds"] = delaySeconds,
        ["via"] = via,
    };

    private HandlerResult Sleep(Guid id, ConnectionSession session) =>
        Deferred(id, new JsonObject { ["action"] = "sleep" }, session, "power.sleep", services.Power.Sleep);

    /// <summary>
    /// Answers "ok" before acting, so it first checks the action can run at all. If it still fails
    /// afterwards, the failure is written to the audit log (the phone sees the PC did not go down).
    /// </summary>
    private HandlerResult Deferred(Guid id, JsonObject response, ConnectionSession session, string type, Func<PowerOutcome> action)
    {
        var available = services.Power.CheckAvailable();
        if (!available.Ok)
        {
            return FromOutcome(id, available, response);
        }
        return new HandlerResult(Responses.Ok(id, response), true, null, async () =>
        {
            await Task.Delay(AfterResponseDelay).ConfigureAwait(false);
            var outcome = action();
            if (!outcome.Ok)
            {
                services.Audit.Record(session.AuthenticatedDevice?.ToString("D"), type + ".deferred", false, outcome.ErrorCode, session.Remote);
            }
        });
    }

    private static HandlerResult FromOutcome(Guid id, PowerOutcome outcome, JsonObject successPayload) =>
        outcome.Ok ? Ok(id, successPayload) : Fail(id, outcome.ErrorCode ?? ErrorCodes.PowerFailed, outcome.Message ?? "Power action failed.");

    private static HandlerResult Ok(Guid id, JsonObject payload) => new(Responses.Ok(id, payload), true, null);

    private static HandlerResult Fail(Guid? id, string code, string message) => new(Responses.Error(id, code, message), false, code);

    private static Guid? TryReadId(string json) => TryRead(json, "id") is { } text && Guid.TryParseExact(text, "D", out var id) ? id : null;

    private static string? TryReadType(string json) => TryRead(json, "type");

    private static string? TryRead(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>At most one audit line per device per minute for successful input events.</summary>
internal sealed class InputAuditThrottle(TimeProvider time, TimeSpan? interval = null)
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public bool ShouldRecord(string? deviceId, string type)
    {
        var key = (deviceId ?? "") + "|" + type;
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_last.TryGetValue(key, out var last) && now - last < _interval)
            {
                return false;
            }
            _last[key] = now;
            return true;
        }
    }
}
