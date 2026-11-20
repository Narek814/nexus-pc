namespace Nexus.Setup;

/// <summary>
/// The data folder failed the check again after the OpenRGB import (R11-1). Deliberately not one of the
/// exception types Program turns into exit code 5: Install catches it and returns <see cref="ExitCode"/>
/// (<see cref="SetupPlan.ExitDataDirectoryUntrusted"/>), with the check's own text as the message.
/// </summary>
internal sealed class DataDirectoryRecheckException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
