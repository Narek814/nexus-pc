using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Core.Power;
using Nexus.Core.Protocol;

namespace Nexus.Windows;

/// <summary>
/// InitiateShutdownW / AbortSystemShutdownW / SetSuspendState. With a delay, Windows shows its own
/// notice with <c>reason</c> to whoever is at the PC, and the shutdown can be cancelled (power.cancel).
/// Requires SeShutdownPrivilege, which the service enables on its own token.
/// </summary>
[SupportedOSPlatform("windows")]
/// <param name="forceOtherSessions">
/// True in the service (session 0, where the signed-in person is "another user"). False in Nexus.Tray,
/// which runs in that person's session, so Windows lets apps ask to save before shutting down.
/// </param>
public sealed partial class WindowsPowerController(bool forceOtherSessions = true) : IPowerController
{
    private const uint ShutdownForceOthers = 0x1;
    private const uint ShutdownForceSelf = 0x2;
    private const uint ShutdownRestart = 0x4;
    private const uint ShutdownPowerOff = 0x8;
    private const uint ReasonFlagPlanned = 0x80000000; // SHTDN_REASON_MAJOR_OTHER | MINOR_OTHER | FLAG_PLANNED
    private const int ErrorShutdownInProgress = 1115;
    private const int ErrorNoShutdownInProgress = 1116;
    private const int ErrorShutdownIsScheduled = 1190;
    private const int ErrorNotAllAssigned = 1300;

    public PowerOutcome CheckAvailable() => EnableShutdownPrivilege() ?? PowerOutcome.Success;

    public PowerOutcome Schedule(PowerAction action, TimeSpan delay, bool force, string reason)
    {
        if (EnableShutdownPrivilege() is { } privilegeError)
        {
            return privilegeError;
        }
        // No SHUTDOWN_HYBRID: a full shutdown keeps Wake-on-LAN working on more hardware than Fast Startup.
        // SHUTDOWN_FORCE_OTHERS is required, not optional: the service runs as SYSTEM in session 0, so the
        // signed-in person counts as "another user" and Windows refuses with ERROR_SHUTDOWN_USERS_LOGGED_ON
        // (found on a real PC). shutdown.exe /t N does the same. Unsaved work is protected by the minimum
        // grace period the dispatcher enforces for non-forced requests (Windows shows its countdown).
        var flags = (action == PowerAction.Restart ? ShutdownRestart : ShutdownPowerOff) | (forceOtherSessions ? ShutdownForceOthers : 0);
        if (force)
        {
            flags |= ShutdownForceSelf;
        }
        var result = (int)InitiateShutdownW(null, reason, (uint)delay.TotalSeconds, flags, ReasonFlagPlanned);
        return result switch
        {
            0 => PowerOutcome.Success,
            ErrorShutdownIsScheduled or ErrorShutdownInProgress =>
                new PowerOutcome(false, ErrorCodes.PowerFailed, "A shutdown or restart is already pending. Cancel it first (power.cancel)."),
            _ => Win32Failure("InitiateShutdown", result),
        };
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
