using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Nexus.Core.Security;

/// <summary>
/// Device authentication primitives. Devices hold a P-256 key (Secure Enclave on iPhone, which
/// supports only P-256) and sign a transcript that binds the agent's nonce to the pinned TLS certificate.
/// </summary>
public static class DeviceCrypto
{
    public const int NonceBytes = 32;
    public const int MaxSpkiBytes = 200;
    public const int MaxSignatureBytes = 128;

    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>SHA-256 of the DER certificate, uppercase hex. Printed in the pairing link and pinned by clients.</summary>
    public static string CertificateFingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
    }

    /// <summary>Bytes the device signs to prove possession of its key during auth.prove.</summary>
    public static byte[] AuthTranscript(Guid pcId, Guid deviceId, ReadOnlySpan<byte> nonce, string certificateFingerprint)
    {
        ArgumentNullException.ThrowIfNull(certificateFingerprint);
        return Encoding.UTF8.GetBytes(
            $"nexuspc-auth-v1\n{pcId:D}\n{deviceId:D}\n{Convert.ToBase64String(nonce)}\n{certificateFingerprint.ToUpperInvariant()}");
    }

    /// <summary>Accepts only uncompressed P-256 keys encoded as SubjectPublicKeyInfo.</summary>
    public static bool IsValidP256PublicKey(ReadOnlySpan<byte> spki)
    {
        if (spki.IsEmpty || spki.Length > MaxSpkiBytes)
        {
            return false;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            return read == spki.Length && key.ExportParameters(false).Curve.Oid.Value == P256Oid;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Verifies a DER (X9.62) ECDSA-SHA256 signature, the format iOS SecKey produces.</summary>
    public static bool VerifySignature(ReadOnlySpan<byte> spki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (!IsValidP256PublicKey(spki) || signature.IsEmpty || signature.Length > MaxSignatureBytes)
        {
            return false;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Short verification code both sides display during pairing, so the person can check the PC is
    /// approving their phone and not another device that saw the pairing code.
    /// </summary>
    public static string ShortAuthenticationString(ReadOnlySpan<byte> spki, string certificateFingerprint)
    {
        ArgumentNullException.ThrowIfNull(certificateFingerprint);
        var prefix = "nexuspc-sas-v1\n"u8;
        var fingerprint = Encoding.UTF8.GetBytes(certificateFingerprint.ToUpperInvariant());
        var buffer = new byte[prefix.Length + spki.Length + fingerprint.Length];
        prefix.CopyTo(buffer);
        spki.CopyTo(buffer.AsSpan(prefix.Length));
        fingerprint.CopyTo(buffer.AsSpan(prefix.Length + spki.Length));
        var hash = SHA256.HashData(buffer);
        return (BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
