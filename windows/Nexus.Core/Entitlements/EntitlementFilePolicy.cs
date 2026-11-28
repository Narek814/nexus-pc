using Nexus.Core.Storage;

namespace Nexus.Core.Entitlements;

/// <summary>
/// Who may touch entitlement.json: a protected DACL whose allow entries name only SYSTEM (plus the test account in
/// tests), owned by a trusted subject. Anything else means someone else wrote or re-permissioned the file: the agent
/// deletes it and treats it as "no token". Decision only (the rule is shared with account.dat in
/// <see cref="ProtectedFilePolicy"/>); the Windows calls live in Nexus.Agent.
/// </summary>
public static class EntitlementFilePolicy
{
    public const string SystemSid = ProtectedFilePolicy.SystemSid;

    public static bool IsTrusted(string? ownerSid, bool daclPresent, bool daclProtected, IReadOnlyList<DataAce> dacl,
        IReadOnlySet<string> allowedAccess, IReadOnlySet<string> trustedOwners) =>
        ProtectedFilePolicy.IsTrusted(ownerSid, daclPresent, daclProtected, dacl, allowedAccess, trustedOwners);
}
