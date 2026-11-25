using System.Runtime.Versioning;
using System.Text.Json;
using Nexus.Core.Entitlements;
using Nexus.Core.Storage;

namespace Nexus.Agent.Storage;

/// <summary>
/// entitlement.json in the protected data folder. On Windows the file gets its own protected DACL with access for
/// SYSTEM only (the test account too in tests); on load the owner and access entries are checked, and a file that
/// fails the check is deleted and treated as "no token". Nothing in it is secret (the token is signed, the counters
/// are not confidential), so there is no DPAPI here; the refresh token (account.dat) is stored with DPAPI LocalMachine
/// and the same SYSTEM-only access (<see cref="ProtectedFile"/>).
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
        using (var stream = ProtectedFile.CreateNew(temp))
        {
            stream.Write(bytes);
        }
        // Rename within the folder keeps the temp file's own descriptor.
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>SYSTEM in production; the test account is added only while AgentPaths.TestTrustedSids is set (tests).</summary>
    [SupportedOSPlatform("windows")]
    internal static IReadOnlySet<string> AllowedAccess() => ProtectedFile.AllowedAccess();

    /// <summary>Only a protected SYSTEM-only file counts (no legacy form for entitlement.json).</summary>
    internal static bool IsTrusted(string path) => ProtectedFile.Classify(path, acceptLegacy: false) == ProtectedFileVerdict.Trusted;
}
