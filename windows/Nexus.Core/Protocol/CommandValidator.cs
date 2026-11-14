using Nexus.Core.Security;

namespace Nexus.Core.Protocol;

public sealed record ValidatedCommand(RequestEnvelope Envelope, CommandSpec Spec, object Payload);

public sealed record ValidationResult(ValidatedCommand? Command, ProtocolError? Error)
{
    public bool IsSuccess => Command is not null;
    internal static ValidationResult Fail(string code, string message) => new(null, new ProtocolError(code, message));
}

/// <summary>
/// Single gate every incoming request passes before any handler runs.
/// Check order (documented in shared/protocol/fixtures): protocol → command.unknown → auth.required
/// → auth.permissionDenied → auth.clockSkew → auth.replay → confirm.required → command.invalidPayload.
/// </summary>
public sealed class CommandValidator
{
    private readonly CommandCatalog _catalog;
    private readonly ReplayGuard _replay;
    private readonly TimeProvider _time;

    public CommandValidator(CommandCatalog catalog, ReplayGuard replay, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(time);
        _catalog = catalog;
        _replay = replay;
        _time = time;
    }

    public static CommandValidator CreateDefault(TimeProvider time) =>
        new(CommandCatalog.V1, new ReplayGuard(), time);

    public ValidationResult Validate(string json, ClientContext client)
    {
        var parsed = EnvelopeParser.Parse(json);
        return parsed.Envelope is null
            ? new ValidationResult(null, parsed.Error)
            : Validate(parsed.Envelope, client);
    }

    public ValidationResult Validate(RequestEnvelope envelope, ClientContext client)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(client);

        if (!_catalog.TryGet(envelope.Type, out var spec))
        {
            return ValidationResult.Fail(ErrorCodes.UnknownCommand, $"Unknown command '{envelope.Type}'.");
        }

        if (spec.RequiresAuth)
        {
            if (!client.IsAuthenticated || client.DeviceId is null)
            {
                return ValidationResult.Fail(ErrorCodes.AuthRequired, "Authentication required.");
            }
            if ((client.Permissions & spec.RequiredPermission) != spec.RequiredPermission)
            {
                return ValidationResult.Fail(ErrorCodes.PermissionDenied, $"Device lacks permission for '{spec.Type}'.");
            }
        }

        var now = _time.GetUtcNow();
        if ((envelope.Timestamp - now).Duration() > ProtocolInfo.MaxClockSkew)
        {
            return ValidationResult.Fail(ErrorCodes.ClockSkew, "Message timestamp is outside the allowed window.");
        }

        if (spec.RequiresAuth)
        {
            switch (_replay.TryRegister(client.DeviceId!, envelope.Id, envelope.Timestamp + ProtocolInfo.MaxClockSkew, now))
            {
                case ReplayResult.Replayed:
                    return ValidationResult.Fail(ErrorCodes.Replay, "Message id was already used.");
                case ReplayResult.CapacityExceeded:
                    return ValidationResult.Fail(ErrorCodes.RateLimited, "Too many messages.");
            }
        }

        if (spec.Risk == RiskClass.Critical && !envelope.Confirmed)
        {
            return ValidationResult.Fail(ErrorCodes.ConfirmationRequired, $"'{spec.Type}' requires confirmation.");
        }

        var payload = spec.ParsePayload(envelope.Payload);
        return payload.Value is null
            ? ValidationResult.Fail(ErrorCodes.InvalidPayload, payload.Error ?? "Invalid payload.")
            : new ValidationResult(new ValidatedCommand(envelope, spec, payload.Value), null);
    }
}
