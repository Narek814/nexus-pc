using System.Reflection;
using System.Security.Cryptography;

namespace Nexus.Setup;

/// <summary>Pinned PawnIO installer: the only file accepted, built in by CI (MSBuild properties PawnIoSha256, PawnIoLeafCertSha256, PawnIoVersion).</summary>
internal sealed record PawnIoPin(string FileSha256, string LeafCertSha256, string Version)
{
    public static PawnIoPin FromAssembly()
    {
        var metadata = typeof(PawnIoPin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value ?? "", StringComparer.Ordinal);
        return new(metadata.GetValueOrDefault("PawnIoSha256", ""), metadata.GetValueOrDefault("PawnIoLeafCertSha256", ""), metadata.GetValueOrDefault("PawnIoVersion", ""));
    }

    public bool Complete => IsSha256(FileSha256) && IsSha256(LeafCertSha256) && Version.Length > 0;

    internal static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <param name="Allowed">Run the file.</param>
/// <param name="RevocationUnknown">Allowed although revocation could not be checked (owner decision 3): the caller warns.</param>
internal sealed record PawnIoVerdict(bool Allowed, bool RevocationUnknown, string Reason);

/// <summary>
/// T5 section 4 and owner decision 3. Order: pins present → file SHA-256 (read through the held handle) → WinVerifyTrust with
/// revocation → exact leaf certificate. Only when the revocation status is unavailable (offline / no check possible) is
/// WinVerifyTrust repeated without revocation; then the file is allowed only if that is valid AND the leaf matches exactly,
/// and the verdict says the revocation status is unknown. A revoked certificate or any other error refuses.
/// </summary>
internal static class PawnIoPolicy
{
    public const int CertERevoked = unchecked((int)0x800B010C);
    public const int CryptERevocationOffline = unchecked((int)0x80092013);
    public const int CryptENoRevocationCheck = unchecked((int)0x80092012);
    public const int CertERevocationFailure = unchecked((int)0x800B010E);

    /// <param name="verify">One WinVerifyTrust call (argument: check revocation) → (HRESULT, leaf SHA-256 when 0).</param>
    public static PawnIoVerdict Check(PawnIoPin pin, string fileSha256, Func<bool, (int Code, string? LeafSha256)> verify)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(fileSha256);
        ArgumentNullException.ThrowIfNull(verify);
        if (!pin.Complete)
        {
            return new(false, false, "no pinned PawnIO hash or certificate in this build");
        }
        if (!SameHex(fileSha256, pin.FileSha256))
        {
            return new(false, false, "file SHA-256 differs from the pinned one");
        }
        var (code, leaf) = verify(true);
        var revocationUnknown = false;
        if (code is CryptERevocationOffline or CryptENoRevocationCheck or CertERevocationFailure)
        {
            (code, leaf) = verify(false);
            revocationUnknown = true;
        }
        if (code != 0)
        {
            return new(false, false, code == CertERevoked
                ? "signing certificate revoked"
                : $"signature not valid (0x{code:X8})");
        }
        if (leaf is null || !SameHex(leaf, pin.LeafCertSha256))
        {
            return new(false, false, "signer certificate is not the pinned one");
        }
        return new(true, revocationUnknown, revocationUnknown ? "valid; revocation status could not be checked" : "valid");
    }

    /// <summary>The started process runs exactly the checked file (QueryFullProcessImageName vs. the staging path).</summary>
    public static bool SameImage(string? actual, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (actual is null)
        {
            return false;
        }
        static string Plain(string p) => p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
        return string.Equals(Plain(actual), Plain(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameHex(string a, string b)
    {
        var x = a.ToLowerInvariant();
        var y = b.ToLowerInvariant();
        return PawnIoPin.IsSha256(x) && PawnIoPin.IsSha256(y)
            && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(x), Convert.FromHexString(y));
    }
}
