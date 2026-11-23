using System.Collections.Frozen;

namespace Nexus.Core.Entitlements;

/// <summary>Signature algorithm pinned for a kid. Only ES256 is verified; any other pinned algorithm is refused (reason "algorithm").</summary>
public enum EntitlementAlgorithm
{
    Es256,
    /// <summary>Pinned for an algorithm this agent does not verify (for example a reserved Ed25519 slot).</summary>
    Unsupported,
}

/// <summary>A server public key pinned by kid. <see cref="SubjectPublicKeyInfo"/> is DER (P-256) for ES256.</summary>
public sealed record EntitlementKey(string Kid, EntitlementAlgorithm Algorithm, byte[]? SubjectPublicKeyInfo);

/// <summary>
/// What the agent trusts for entitlement tokens: pinned keys by kid (compared Ordinal), revoked kids and the
/// issuer. Production uses <see cref="PinnedEntitlementKeys.Trust"/>; tests construct their own with test keys.
/// </summary>
public sealed class EntitlementTrust
{
    public EntitlementTrust(IEnumerable<EntitlementKey> keys, IEnumerable<string> revokedKids, string issuer)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(revokedKids);
        ArgumentException.ThrowIfNullOrEmpty(issuer);
        Keys = keys.ToFrozenDictionary(k => k.Kid, StringComparer.Ordinal);
        RevokedKids = revokedKids.ToFrozenSet(StringComparer.Ordinal);
        Issuer = issuer;
    }

    public IReadOnlyDictionary<string, EntitlementKey> Keys { get; }
    public IReadOnlySet<string> RevokedKids { get; }
    public string Issuer { get; }

    /// <summary>
    /// True when at least one key is pinned. With an empty table no token can be accepted, so the agent keeps
    /// today's behaviour (no plan checks) instead of turning every Pro feature off; see the ENT-2 report.
    /// </summary>
    public bool IsConfigured => Keys.Count > 0;
}

/// <summary>
/// Values built into the agent. Real server keys do not exist yet: the table is empty in every build and every
/// token is refused as "unknownKid". At release it gets one working and two spare ES256 keys (SECURITY.md,
/// «Аккаунты и права»). Never put fixture or test keys here: a test checks that no kid starts with "test-".
/// </summary>
public static class PinnedEntitlementKeys
{
    /// <summary>Placeholder issuer: the production value is not chosen yet (filled in at release together with the keys).</summary>
    public const string Issuer = "nexus-issuer-unset";

    /// <summary>Working key, then two spares. Empty until the release keys exist.</summary>
    public static readonly IReadOnlyList<EntitlementKey> Keys = [];

    /// <summary>Kids revoked by an agent update (ordinal).</summary>
    public static readonly IReadOnlyList<string> RevokedKids = [];

    public static EntitlementTrust Trust { get; } = new(Keys, RevokedKids, Issuer);
}
