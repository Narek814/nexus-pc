using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Core.Protocol;

namespace Nexus.Agent.Power;

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
internal sealed class UnsupportedPowerController : IPowerController
{
    private static readonly PowerOutcome Unsupported = new(false, ErrorCodes.PowerUnsupported, "Power control is only available on Windows.");

    public PowerOutcome CheckAvailable() => Unsupported;

    public PowerOutcome Schedule(PowerAction action, TimeSpan delay, bool force, string reason) => Unsupported;

    public PowerOutcome Sleep() => Unsupported;

    public PowerOutcome Cancel() => Unsupported;
}

/// <summary>
/// InitiateShutdownW / AbortSystemShutdownW / SetSuspendState. With a delay, Windows shows its own
/// notice with <c>reason</c> to whoever is at the PC, and the shutdown can be cancelled (power.cancel).
/// Requires SeShutdownPrivilege, which the service enables on its own token.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPowerController : IPowerController
{
    private const uint ShutdownForceOthers = 0x1;
    private const uint ShutdownForceSelf = 0x2;
    private const uint ShutdownRestart = 0x4;
    private const uint ShutdownPowerOff = 0x8;
    private const uint ReasonFlagPlanned = 0x80000000; // SHTDN_REASON_MAJOR_OTHER | MINOR_OTHER | FLAG_PLANNED
    private const int ErrorNoShutdownInProgress = 1116;
    private const int ErrorNotAllAssigned = 1300;

    public PowerOutcome CheckAvailable() => EnableShutdownPrivilege() ?? PowerOutcome.Success;

    public PowerOutcome Schedule(PowerAction action, TimeSpan delay, bool force, string reason)
    {
        if (EnableShutdownPrivilege() is { } privilegeError)
        {
            return privilegeError;
        }
        // No SHUTDOWN_HYBRID: a full shutdown keeps Wake-on-LAN working on more hardware than Fast Startup.
        var flags = action == PowerAction.Restart ? ShutdownRestart : ShutdownPowerOff;
        if (force)
        {
            flags |= ShutdownForceOthers | ShutdownForceSelf;
        }
        var result = InitiateShutdownW(null, reason, (uint)delay.TotalSeconds, flags, ReasonFlagPlanned);
        return result == 0 ? PowerOutcome.Success : Win32Failure("InitiateShutdown", (int)result);
    }

    public PowerOutcome Sleep()
    {
        if (EnableShutdownPrivilege() is { } privilegeError)
        {
            return privilegeError;
        }
        return SetSuspendState(hibernate: false, force: false, disableWakeEvent: false)
            ? PowerOutcome.Success
            : Win32Failure("SetSuspendState", Marshal.GetLastPInvokeError());
    }

    public PowerOutcome Cancel()
    {
        if (EnableShutdownPrivilege() is { } privilegeError)
        {
            return privilegeError;
        }
        if (AbortSystemShutdownW(null))
        {
            return PowerOutcome.Success;
        }
        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNoShutdownInProgress
            ? new PowerOutcome(false, ErrorCodes.PowerNothingToCancel, "No shutdown or restart is pending.")
            : Win32Failure("AbortSystemShutdown", error);
    }

    private static PowerOutcome? EnableShutdownPrivilege()
    {
        const uint TokenAdjustPrivileges = 0x20;
        const uint TokenQuery = 0x8;
        const uint PrivilegeEnabled = 0x2;

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return Win32Failure("OpenProcessToken", Marshal.GetLastPInvokeError());
        }
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
            {
                return Win32Failure("LookupPrivilegeValue", Marshal.GetLastPInvokeError());
            }
            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = PrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return Win32Failure("AdjustTokenPrivileges", Marshal.GetLastPInvokeError());
            }
            // AdjustTokenPrivileges "succeeds" without the privilege; the real answer is in the last error.
            return Marshal.GetLastPInvokeError() == ErrorNotAllAssigned
                ? new PowerOutcome(false, ErrorCodes.PowerFailed, "The agent's account does not hold SeShutdownPrivilege.")
                : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static PowerOutcome Win32Failure(string api, int error) =>
        new(false, ErrorCodes.PowerFailed, $"{api} failed with Win32 error {error}.");

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint InitiateShutdownW(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AbortSystemShutdownW(string? machineName);

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
