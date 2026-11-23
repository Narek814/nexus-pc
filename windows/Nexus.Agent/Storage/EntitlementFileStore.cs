using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Nexus.Core.Entitlements;
using Nexus.Core.Storage;

namespace Nexus.Agent.Storage;

/// <summary>
/// entitlement.json in the protected data folder. On Windows the file gets its own protected DACL with access for
/// SYSTEM only (the test account too in tests); on load the owner and access entries are checked, and a file that
/// fails the check is deleted and treated as "no token". Nothing in it is secret (the token is signed, the counters
/// are not confidential), so there is no DPAPI here; the refresh token of E4 will be stored with DPAPI LocalMachine.
/// </summary>
internal sealed class EntitlementFileStore(string dataDirectory) : IEntitlementStore
{
    public const string FileName = DataDirectoryPolicy.EntitlementFileName;
    private const int MaxBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
    };

    public string FilePath { get; } = Path.Combine(dataDirectory, FileName);

    /// <summary>Set when the last load found a file with foreign access and deleted it (logged by the caller).</summary>
    public bool DiscardedUntrusted { get; private set; }

    public EntitlementRecord? Load()
    {
        DiscardedUntrusted = false;
        File.Delete(FilePath + ".tmp");
        if (!File.Exists(FilePath))
        {
            return null;
        }
        if (!IsTrusted(FilePath))
        {
            File.Delete(FilePath);
            DiscardedUntrusted = true;
            return null;
        }
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Length > MaxBytes)
            {
                return null;
            }
            return JsonSerializer.Deserialize<EntitlementRecord>(File.ReadAllBytes(FilePath), Json) is { V: 1 } record ? record : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(EntitlementRecord record)
    {
        var temp = FilePath + ".tmp";
        File.Delete(temp);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (OperatingSystem.IsWindows())
        {
            using var stream = CreateProtected(temp);
            stream.Write(bytes);
        }
        else
        {
            using var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(bytes);
        }
        // Rename within the folder keeps the temp file's own descriptor.
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>SYSTEM in production; the test account is added only while AgentPaths.TestTrustedSids is set (tests).</summary>
    [SupportedOSPlatform("windows")]
    internal static IReadOnlySet<string> AllowedAccess()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EntitlementFilePolicy.SystemSid };
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

    [SupportedOSPlatform("windows")]
    private static FileStream CreateProtected(string path)
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

    internal static bool IsTrusted(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsTrustedWindows(path);
        }
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrustedWindows(string path)
    {
        try
        {
            var sddl = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            var descriptor = new RawSecurityDescriptor(sddl);
            var aces = new List<DataAce>();
            if (descriptor.DiscretionaryAcl is { } dacl)
            {
                foreach (var ace in dacl)
                {
                    if (ace is not KnownAce known)
                    {
                        return false; // object or callback entries: not something the agent writes
                    }
                    aces.Add(new DataAce(known.SecurityIdentifier.Value, known.AceType is not (AceType.AccessDenied or AceType.AccessDeniedObject
                        or AceType.AccessDeniedCallback or AceType.AccessDeniedCallbackObject), (uint)known.AccessMask, (int)known.AceFlags));
                }
            }
            return EntitlementFilePolicy.IsTrusted(descriptor.Owner?.Value, descriptor.DiscretionaryAcl is not null,
                (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, aces,
                AllowedAccess(), AgentPaths.TrustedSids(AgentPaths.TestTrustedSids));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
