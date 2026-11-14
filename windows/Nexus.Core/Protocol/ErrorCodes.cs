namespace Nexus.Core.Protocol;

/// <summary>Stable wire error codes (docs/API.md). Never rename: clients switch on them.</summary>
public static class ErrorCodes
{
    public const string Malformed = "protocol.malformed";
    public const string TooLarge = "protocol.tooLarge";
    public const string UnsupportedVersion = "protocol.unsupportedVersion";
    public const string UnknownCommand = "command.unknown";
    public const string InvalidPayload = "command.invalidPayload";
    public const string AuthRequired = "auth.required";
    public const string PermissionDenied = "auth.permissionDenied";
    public const string ClockSkew = "auth.clockSkew";
    public const string Replay = "auth.replay";
    public const string RateLimited = "rate.limited";
    public const string ConfirmationRequired = "confirm.required";
}

public sealed record ProtocolError(string Code, string Message);
