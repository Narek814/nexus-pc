using Nexus.Core.Protocol;

namespace Nexus.Core.Power;

public enum PowerAction
{
    Shutdown,
    Restart,
}

public sealed record PowerOutcome(bool Ok, string? ErrorCode = null, string? Message = null)
{
    public static PowerOutcome Success { get; } = new(true);
}

public interface IPowerController
{
    /// <summary>Whether actions can run at all (platform, privilege), without doing anything.</summary>
    PowerOutcome CheckAvailable();

    PowerOutcome Schedule(PowerAction action, TimeSpan delay, bool force, string reason);

    PowerOutcome Sleep();

    PowerOutcome Cancel();
}

/// <summary>Non-Windows hosts (development, tests): refuses honestly instead of pretending.</summary>
public sealed class UnsupportedPowerController : IPowerController
{
    private static readonly PowerOutcome Unsupported = new(false, ErrorCodes.PowerUnsupported, "Power control is only available on Windows.");

    public PowerOutcome CheckAvailable() => Unsupported;

    public PowerOutcome Schedule(PowerAction action, TimeSpan delay, bool force, string reason) => Unsupported;

    public PowerOutcome Sleep() => Unsupported;

    public PowerOutcome Cancel() => Unsupported;
}
