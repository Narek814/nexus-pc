using System.Security.Cryptography;

namespace Nexus.Core.Security;

/// <summary>
/// pcId derived from the PC's permanent key (shared/fixtures/pc-id/derive.v1.json):
/// u = SHA-256("nexus-pc-id-v1\n" || SPKI DER)[0..16], version 8, RFC 9562 variant.
/// </summary>
public static class PcIdDerivation
{
    /// <summary>DER of a P-256 SubjectPublicKeyInfo with an uncompressed point: 26-byte header + 0x04 || X || Y.</summary>
    public const int P256SpkiLength = 91;

    private static ReadOnlySpan<byte> Domain => "nexus-pc-id-v1\n"u8;

    private static ReadOnlySpan<byte> P256Header =>
    [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
        0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    ];

    /// <summary>True for exactly the 91-byte DER of a P-256 key with an uncompressed point.</summary>
    public static bool IsP256Spki(ReadOnlySpan<byte> spki) =>
        spki.Length == P256SpkiLength && spki[..P256Header.Length].SequenceEqual(P256Header);

    /// <exception cref="ArgumentException">Not a P-256 uncompressed SPKI.</exception>
    public static Guid FromSpki(ReadOnlySpan<byte> spki)
    {
        if (!IsP256Spki(spki))
        {
            throw new ArgumentException("SPKI must be a P-256 key with an uncompressed point.", nameof(spki));
        }
        Span<byte> input = stackalloc byte[Domain.Length + P256SpkiLength];
        Domain.CopyTo(input);
        spki.CopyTo(input[Domain.Length..]);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        var u = hash[..16];
        u[6] = (byte)((u[6] & 0x0F) | 0x80);
        u[8] = (byte)((u[8] & 0x3F) | 0x80);
        return new Guid(u, bigEndian: true);
    }

    /// <summary>A pcId from <see cref="FromSpki"/> (version 8); random v4 ids come from installations before E4.</summary>
    public static bool IsDerived(Guid pcId) => pcId.Version == 8;
}
