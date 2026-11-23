using Nexus.Core.Storage;

namespace Nexus.Core.Entitlements;

/// <summary>
/// Who may touch entitlement.json: a protected DACL whose allow entries name only SYSTEM (plus the test account in
/// tests), owned by a trusted subject. Anything else means someone else wrote or re-permissioned the file: the agent
/// deletes it and treats it as "no token". Decision only; the Windows calls live in Nexus.Agent.
/// </summary>
public static class EntitlementFilePolicy
{
    public const string SystemSid = "S-1-5-18";

    public static bool IsTrusted(string? ownerSid, bool daclPresent, bool daclProtected, IReadOnlyList<DataAce> dacl,
        IReadOnlySet<string> allowedAccess, IReadOnlySet<string> trustedOwners)
    {
        ArgumentNullException.ThrowIfNull(dacl);
        ArgumentNullException.ThrowIfNull(allowedAccess);
        ArgumentNullException.ThrowIfNull(trustedOwners);
        return ownerSid is not null && trustedOwners.Contains(ownerSid) && daclPresent && daclProtected
            && dacl.All(ace => !ace.Allow || allowedAccess.Contains(ace.Sid));
    }
}
