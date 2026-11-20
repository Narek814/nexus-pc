using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Nexus.Core.Storage;

namespace Nexus.Windows;

/// <summary>
/// The lock shared by "datadir" commands, installation and removal (SA-T4-R6-N1, N2): a mutex inside a
/// private namespace whose boundary descriptor holds BUILTIN\Administrators.
/// <list type="bullet">
/// <item>Creating or opening the namespace requires a token in which Administrators is an enabled group: an
/// elevated administrator or SYSTEM. An ordinary user, or the same administrator without elevation (the
/// group is deny-only there), can neither create it first nor open it.</item>
/// <item>The namespace and the mutex get a DACL with SYSTEM and Administrators only.</item>
/// <item>A namespace an ordinary user makes under the same alias has another boundary and is another
/// namespace: it cannot take this lock's name.</item>
/// </list>
/// The old Global\NexusPC.DataDir name is not used any more (anyone could take it).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ProtectedDataDirectoryLock : IDataDirectoryLockProvider
{
    public const string BoundaryName = "NexusPC.DataDir";
    public const string NamespaceAlias = "NexusPC";
    public const string MutexName = NamespaceAlias + @"\DataDir";

    private const int ErrorAlreadyExists = 183;
    private const int ErrorPathNotFound = 3;
    private const int ErrorFileNotFound = 2;

    public LockOutcome TryAcquire(out IDisposable? held, out string? failure)
    {
        held = null;
        failure = null;
        var boundary = CreateBoundaryDescriptorW(BoundaryName, 0);
        if (boundary == IntPtr.Zero)
        {
            failure = Win32("boundary descriptor");
            return LockOutcome.Unavailable;
        }
        IntPtr ns = IntPtr.Zero;
        try
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var sid = new byte[administrators.BinaryLength];
            administrators.GetBinaryForm(sid, 0);
            if (!AddSIDToBoundaryDescriptor(ref boundary, sid))
            {
                failure = Win32("boundary SID");
                return LockOutcome.Unavailable;
            }
            ns = OpenOrCreateNamespace(boundary, out failure);
            if (ns == IntPtr.Zero)
            {
                return LockOutcome.Unavailable;
            }

            Mutex mutex;
            try
            {
                mutex = MutexAcl.Create(false, MutexName, out _, ProtectedMutexSecurity());
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                failure = Describe("create or open the lock", ex);
                return LockOutcome.Unavailable;
            }
            // SA-T4-R7-1: whether made now or found, the mutex is used only if its owner and DACL are exactly ours.
            if (SecurityRefusal(mutex) is { } refusal)
            {
                mutex.Dispose();
                failure = refusal;
                return LockOutcome.Unavailable;
            }
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // the previous holder died mid-command: its journal tells what is unfinished
            }
            if (!owned)
            {
                mutex.Dispose();
                return LockOutcome.Busy;
            }
            held = new Held(mutex, ns);
            ns = IntPtr.Zero; // owned by Held now
            return LockOutcome.Acquired;
        }
        finally
        {
            if (ns != IntPtr.Zero)
            {
                ClosePrivateNamespace(ns, 0);
            }
            DeleteBoundaryDescriptor(boundary);
        }
    }

    /// <summary>The namespace with a SYSTEM/Administrators-only DACL; opened if another command already made it.</summary>
    private static IntPtr OpenOrCreateNamespace(IntPtr boundary, out string? error)
    {
        error = null;
        var descriptor = new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var code = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            IntPtr ns;
            unsafe
            {
                fixed (byte* sd = bytes)
                {
                    var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), Descriptor = (IntPtr)sd, InheritHandle = 0 };
                    ns = CreatePrivateNamespaceW(ref attributes, boundary, NamespaceAlias);
                    code = Marshal.GetLastPInvokeError();
                }
            }
            if (ns != IntPtr.Zero)
            {
                return ns;
            }
            if (code != ErrorAlreadyExists)
            {
                error = Describe("create the private namespace", code);
                return IntPtr.Zero;
            }
            ns = OpenPrivateNamespaceW(boundary, NamespaceAlias);
            if (ns != IntPtr.Zero)
            {
                return ns;
            }
            code = Marshal.GetLastPInvokeError();
            if (code is not (ErrorPathNotFound or ErrorFileNotFound))
            {
                error = Describe("open the private namespace", code);
                return IntPtr.Zero;
            }
            // Closed by its creator between the two calls: try again.
        }
        error = Describe("open the private namespace (it kept disappearing)", code);
        return IntPtr.Zero;
    }

    /// <summary>Owner and DACL of the held mutex handle, checked by ProtectedLockPolicy; null when acceptable.</summary>
    private static string? SecurityRefusal(Mutex mutex)
    {
        try
        {
            // Read from the handle already held (not by name again): the very object that would be waited on.
            var raw = new RawSecurityDescriptor(mutex.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
            var aces = new List<DataAce>();
            if (raw.DiscretionaryAcl is { } dacl)
            {
                foreach (GenericAce ace in dacl)
                {
                    aces.Add(ace is KnownAce known
                        ? new DataAce(known.SecurityIdentifier.Value, ace.AceType == AceType.AccessAllowed, unchecked((uint)known.AccessMask), (int)ace.AceFlags)
                        : new DataAce("(ACE type " + ((int)ace.AceType).ToString(CultureInfo.InvariantCulture) + ")", false, 0, (int)ace.AceFlags));
                }
            }
            return ProtectedLockPolicy.Refusal(raw.Owner?.Value,
                (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && raw.DiscretionaryAcl is not null,
                (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, aces);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or ArgumentException)
        {
            return Describe("read the lock's security", ex);
        }
    }

    public static MutexSecurity ProtectedMutexSecurity()
    {
        var security = new MutexSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static string Win32(string what) => Describe("prepare the " + what, Marshal.GetLastPInvokeError());

    private static string Describe(string what, int code) =>
        $"cannot {what}: {new Win32Exception(code).Message} (error {code.ToString(CultureInfo.InvariantCulture)})";

    // Diagnostics only: the Win32 code and HRESULT behind a managed failure, so one refusal can be told from another.
    private static string Describe(string what, Exception ex) =>
        $"cannot {what}: {ex.GetType().Name}: {ex.Message} (error {(ex.HResult & 0xFFFF).ToString(CultureInfo.InvariantCulture)}, HRESULT 0x{ex.HResult.ToString("X8", CultureInfo.InvariantCulture)})";

    private sealed class Held(Mutex mutex, IntPtr ns) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            mutex.ReleaseMutex();
            mutex.Dispose();
            ClosePrivateNamespace(ns, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateBoundaryDescriptorW(string name, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddSIDToBoundaryDescriptor(ref IntPtr boundaryDescriptor, byte[] requiredSid);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteBoundaryDescriptor(IntPtr boundaryDescriptor);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreatePrivateNamespaceW(ref SecurityAttributes attributes, IntPtr boundaryDescriptor, string aliasPrefix);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenPrivateNamespaceW(IntPtr boundaryDescriptor, string aliasPrefix);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrivateNamespace(IntPtr handle, uint flags);
}
