using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Agent.Power;
using Nexus.Core.Pairing;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Agent.Server;

/// <summary>Validates every request through <see cref="CommandValidator"/>, then runs its handler.</summary>
internal sealed class CommandDispatcher(AgentServices services)
{
    /// <summary>Commands with a working handler in this build; system.hello reports exactly these.</summary>
    public static readonly IReadOnlyList<string> Implemented =
    [
        "system.hello", "pair.request", "auth.begin", "auth.prove", "pc.status",
        "power.shutdown", "power.restart", "power.sleep", "power.cancel",
    ];

    private static readonly Dictionary<string, string> NotYet = new(StringComparer.Ordinal)
    {
        ["power.lock"] = "Locking needs Nexus.Tray in the user session (P1c).",
        ["apps.list"] = "App launcher arrives with Nexus.Tray (P1c).",
        ["apps.launch"] = "App launcher arrives with Nexus.Tray (P1c).",
        ["apps.close"] = "App launcher arrives with Nexus.Tray (P1c).",
        ["profiles.list"] = "Profiles arrive with Nexus.Tray (P1c).",
        ["profiles.run"] = "Profiles arrive with Nexus.Tray (P1c).",
        ["profiles.upsert"] = "Profiles arrive with Nexus.Tray (P1c).",
        ["profiles.delete"] = "Profiles arrive with Nexus.Tray (P1c).",
        ["metrics.snapshot"] = "Metrics arrive in P1d.",
    };

    /// <summary>
    /// Minimum countdown for a shutdown/restart without force. The service has to force-close other
    /// sessions (see WindowsPowerController), so this warning is what protects unsaved work until P1c
    /// routes non-forced requests through Nexus.Tray in the user's session.
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

        services.Audit.Record(session.CurrentClient().DeviceId ?? client.DeviceId, command.Spec.Type, result.Ok, result.ErrorCode, session.Remote);
        return result;
    }

    private async Task<HandlerResult> RunAsync(ValidatedCommand command, ConnectionSession session, CancellationToken cancellationToken)
    {
        var id = command.Envelope.Id;
        if (NotYet.TryGetValue(command.Spec.Type, out var reason))
        {
            return Fail(id, ErrorCodes.NotImplemented, reason);
        }

        switch (command.Payload)
        {
            case PairRequestPayload pair:
                return await PairAsync(id, pair, session, cancellationToken).ConfigureAwait(false);
            case AuthBeginPayload begin:
                return AuthBegin(id, begin, session);
            case AuthProvePayload prove:
                return AuthProve(id, prove, session);
        }

        return command.Spec.Type switch
        {
            "system.hello" => Ok(id, Hello(session)),
            "pc.status" => Ok(id, Status()),
            "power.shutdown" or "power.restart" => Power(id, command, session),
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
        ["capabilities"] = new JsonArray([.. Implemented.Select(t => (JsonNode)t)]),
    };

    private JsonObject Status() => new()
    {
        ["pcId"] = services.Identity.PcId.ToString("D"),
        ["pcName"] = services.Identity.PcName,
        ["os"] = RuntimeInformation.OSDescription,
        ["uptimeSeconds"] = Environment.TickCount64 / 1000,
        ["agentVersion"] = services.Identity.Version,
        // Honest: there is no user-session companion yet, so desktop actions are unavailable.
        ["userSession"] = "unavailable",
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

    private HandlerResult Power(Guid id, ValidatedCommand command, ConnectionSession session)
    {
        var payload = (PowerPayload)command.Payload;
        // Without force, the person at the PC always gets a visible countdown to save or cancel.
        var delaySeconds = payload.Force ? payload.DelaySeconds : Math.Max(payload.DelaySeconds, MinGraceSeconds);
        var action = command.Spec.Type == "power.restart" ? PowerAction.Restart : PowerAction.Shutdown;
        var verb = action == PowerAction.Restart ? "restart" : "shutdown";
        var device = session.AuthenticatedDevice is { } deviceId ? services.Devices.Find(deviceId) : null;
        var reason = $"Nexus PC: {verb} requested from {device?.Name ?? "a paired device"}.";
        var response = new JsonObject
        {
            ["action"] = verb,
            ["scheduledAt"] = services.Time.GetUtcNow().AddSeconds(delaySeconds).ToString("O"),
            ["delaySeconds"] = delaySeconds,
        };

        if (delaySeconds > 0)
        {
            return FromOutcome(id, services.Power.Schedule(action, TimeSpan.FromSeconds(delaySeconds), payload.Force, reason), response);
        }
        // Immediate (force only): respond first, because the PC may stop answering a moment later.
        return Deferred(id, response, session, command.Spec.Type, () => services.Power.Schedule(action, TimeSpan.Zero, payload.Force, reason));
    }

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
