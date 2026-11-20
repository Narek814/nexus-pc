using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Nexus.Tests;

/// <summary>
/// Test helper: runs code on a new thread impersonating this process's own token with BUILTIN\Administrators
/// made deny-only (CreateRestrictedToken). Nothing on the system is changed; the token lives only for the call.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RestrictedToken
{
    private const uint TokenDuplicate = 0x0002, TokenQuery = 0x0008, TokenImpersonate = 0x0004, TokenAssignPrimary = 0x0001;

    public static T RunWithoutAdministrators<T>(Func<T> action)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenImpersonate | TokenAssignPrimary, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sid = new byte[administrators.BinaryLength];
        administrators.GetBinaryForm(sid, 0);
        var pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
        try
        {
            var disable = new SidAndAttributes { Sid = pinned.AddrOfPinnedObject(), Attributes = 0 };
            if (!CreateRestrictedToken(token, 0, 1, [disable], 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                T result = default!;
                Exception? failure = null;
                var thread = new Thread(() =>
                {
                    if (!ImpersonateLoggedOnUser(restricted))
                    {
                        failure = new Win32Exception(Marshal.GetLastWin32Error());
                        return;
                    }
                    try
                    {
                        result = action();
                    }
                    finally
                    {
                        RevertToSelf();
                    }
                });
                thread.Start();
                thread.Join();
                return failure is null ? result : throw failure;
            }
            finally
            {
                CloseHandle(restricted);
            }
        }
        finally
        {
            pinned.Free();
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableSidCount, SidAndAttributes[] sidsToDisable,
        uint deletePrivilegeCount, IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateLoggedOnUser(IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
