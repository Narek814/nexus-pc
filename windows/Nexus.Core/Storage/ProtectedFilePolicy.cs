namespace Nexus.Core.Storage;

/// <summary>What the agent does with a SYSTEM-only file it finds (entitlement.json, account.dat).</summary>
public enum ProtectedFileVerdict
{
    /// <summary>Protected DACL, allow entries only for the allowed set, trusted owner: read it.</summary>
    Trusted,
    /// <summary>
    /// The access account.dat had before E4-6: inherited from the data folder, allow entries only for SYSTEM and
    /// Administrators, owner SYSTEM or Administrators, no deny entries. Read once and rewrite SYSTEM-only.
    /// </summary>
    Legacy,
    /// <summary>Anything else: someone else wrote or re-permissioned the file. Do not read; delete.</summary>
    Untrusted,
}

/// <summary>
/// Decision on the owner and DACL of a SYSTEM-only file in the data folder (no Windows calls; tested on Linux).
/// The same rule for entitlement.json and account.dat; only account.dat accepts <see cref="ProtectedFileVerdict.Legacy"/>.
/// </summary>
public static class ProtectedFilePolicy
{
    public const string SystemSid = DataDirectoryPolicy.LocalSystem;

    /// <summary>ACE flag INHERITED_ACE.</summary>
    public const int InheritedAceFlag = 0x10;

    public static bool IsTrusted(string? ownerSid, bool daclPresent, bool daclProtected, IReadOnlyList<DataAce> dacl,
        IReadOnlySet<string> allowedAccess, IReadOnlySet<string> trustedOwners)
    {
        ArgumentNullException.ThrowIfNull(dacl);
        ArgumentNullException.ThrowIfNull(allowedAccess);
        ArgumentNullException.ThrowIfNull(trustedOwners);
        return ownerSid is not null && trustedOwners.Contains(ownerSid) && daclPresent && daclProtected
            && dacl.All(ace => !ace.Allow || allowedAccess.Contains(ace.Sid));
    }

    /// <summary>The old inherited access (see <see cref="ProtectedFileVerdict.Legacy"/>); nothing else qualifies.</summary>
    public static bool IsLegacyInherited(string? ownerSid, bool daclPresent, bool daclProtected, IReadOnlyList<DataAce> dacl)
    {
        ArgumentNullException.ThrowIfNull(dacl);
        return ownerSid is DataDirectoryPolicy.LocalSystem or DataDirectoryPolicy.Administrators
            && daclPresent && !daclProtected && dacl.Count > 0
            && dacl.All(ace => ace.Allow && (ace.Flags & InheritedAceFlag) != 0
                && ace.Sid is DataDirectoryPolicy.LocalSystem or DataDirectoryPolicy.Administrators);
    }

    public static ProtectedFileVerdict Classify(string? ownerSid, bool daclPresent, bool daclProtected, IReadOnlyList<DataAce> dacl,
        IReadOnlySet<string> allowedAccess, IReadOnlySet<string> trustedOwners, bool acceptLegacy) =>
        IsTrusted(ownerSid, daclPresent, daclProtected, dacl, allowedAccess, trustedOwners) ? ProtectedFileVerdict.Trusted
        : acceptLegacy && IsLegacyInherited(ownerSid, daclPresent, daclProtected, dacl) ? ProtectedFileVerdict.Legacy
        : ProtectedFileVerdict.Untrusted;
}
