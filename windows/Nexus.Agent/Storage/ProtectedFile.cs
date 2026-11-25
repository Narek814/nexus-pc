using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Nexus.Core.Storage;

namespace Nexus.Agent.Storage;

/// <summary>
/// Files in the data folder with their own SYSTEM-only access (entitlement.json, account.dat): creation already
/// protected and the owner/DACL check. The decision itself is <see cref="ProtectedFilePolicy"/> (Nexus.Core).
/// </summary>
internal static class ProtectedFile
{
    /// <summary>SYSTEM in production; the test account is added only while AgentPaths.TestTrustedSids is set (tests).</summary>
    [SupportedOSPlatform("windows")]
    internal static IReadOnlySet<string> AllowedAccess()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProtectedFilePolicy.SystemSid };
        if (AgentPaths.TestTrustedSids is not null)
        {
            using var self = WindowsIdentity.GetCurrent();
            if (self.User is { } user)
            {
                allowed.Add(user.Value);
            }
        }
        return allowed;
    }

    /// <summary>
    /// A new file (FileMode.CreateNew) for writing. Windows: created with the protected SYSTEM-only DACL in the same
    /// call, so it never exists with wider access. Elsewhere (development): mode 0600.
    /// </summary>
    internal static FileStream CreateNew(string path) => OperatingSystem.IsWindows()
        ? CreateProtected(path)
        : new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

    [SupportedOSPlatform("windows")]
    internal static FileStream CreateProtected(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedAccess())
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData | FileSystemRights.Synchronize,
            FileShare.None, 4096, FileOptions.None, security);
    }

    /// <summary>Check by name (entitlement.json). Any error reading the descriptor is <see cref="ProtectedFileVerdict.Untrusted"/>.</summary>
    internal static ProtectedFileVerdict Classify(string path, bool acceptLegacy)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ClassifyUnix(File.GetUnixFileMode(path));
        }
        try
        {
            return ClassifyWindows(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), acceptLegacy);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return ProtectedFileVerdict.Untrusted;
        }
    }

    /// <summary>Check through an open handle (account.dat): what is checked is what is then read.</summary>
    internal static ProtectedFileVerdict Classify(FileStream opened, bool acceptLegacy)
    {
        ArgumentNullException.ThrowIfNull(opened);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return ClassifyUnix(File.GetUnixFileMode(opened.SafeFileHandle));
            }
            return ClassifyWindows(opened.GetAccessControl(), acceptLegacy);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return ProtectedFileVerdict.Untrusted;
        }
    }

    private static ProtectedFileVerdict ClassifyUnix(UnixFileMode mode) =>
        (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0
            ? ProtectedFileVerdict.Trusted
            : ProtectedFileVerdict.Untrusted;

    [SupportedOSPlatform("windows")]
    private static ProtectedFileVerdict ClassifyWindows(FileSecurity security, bool acceptLegacy)
    {
        var sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        var descriptor = new RawSecurityDescriptor(sddl);
        var aces = new List<DataAce>();
        if (descriptor.DiscretionaryAcl is { } dacl)
        {
            foreach (var ace in dacl)
            {
                if (ace is not KnownAce known)
                {
                    return ProtectedFileVerdict.Untrusted; // object or callback entries: not something the agent writes
                }
                aces.Add(new DataAce(known.SecurityIdentifier.Value, known.AceType is not (AceType.AccessDenied or AceType.AccessDeniedObject
                    or AceType.AccessDeniedCallback or AceType.AccessDeniedCallbackObject), (uint)known.AccessMask, (int)known.AceFlags));
            }
        }
        return ProtectedFilePolicy.Classify(descriptor.Owner?.Value, descriptor.DiscretionaryAcl is not null,
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, aces,
            AllowedAccess(), AgentPaths.TrustedSids(AgentPaths.TestTrustedSids), acceptLegacy);
    }
}
