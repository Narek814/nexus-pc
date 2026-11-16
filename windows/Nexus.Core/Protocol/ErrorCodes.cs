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
    public const string AuthFailed = "auth.failed";
    public const string NotImplemented = "command.notImplemented";
    public const string SessionUnavailable = "session.unavailable";
    public const string PairNoActiveSession = "pair.noActiveSession";
    public const string PairInvalidCode = "pair.invalidCode";
    public const string PairBusy = "pair.busy";
    public const string PairRejected = "pair.rejected";
    public const string PairExpired = "pair.expired";
    public const string PairDeviceLimit = "pair.deviceLimit";
    public const string PowerFailed = "power.failed";
    public const string PowerNothingToCancel = "power.nothingToCancel";
    public const string PowerUnsupported = "power.unsupported";
    public const string AppNotFound = "apps.notFound";
    public const string AppLaunchFailed = "apps.launchFailed";
    public const string AppCloseNotAllowed = "apps.closeNotAllowed";
    public const string ProfileNotFound = "profiles.notFound";
    public const string ProfileInvalid = "profiles.invalid";
    public const string ProfileLimit = "profiles.limit";
    public const string LockFailed = "power.lockFailed";
    public const string RgbUnavailable = "rgb.unavailable";
    public const string InputBlocked = "input.blocked";
    public const string Internal = "internal";
}

public sealed record ProtocolError(string Code, string Message);
