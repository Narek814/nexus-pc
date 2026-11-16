using System.Text;
using System.Text.Json;
using Nexus.Core.Pairing;
using Nexus.Core.Security;

namespace Nexus.Tests;

/// <summary>Vectors shared with Swift NexusKit (ios/NexusKit/Tests): both languages must agree byte for byte.</summary>
public class CrossLanguageFixtureTests
{
    [Fact]
    public void Verification_codes_match_vectors()
    {
        using var doc = Shared.Load("fixtures/crypto.v1.json");
        foreach (var v in doc.RootElement.GetProperty("verificationCodes").EnumerateArray())
        {
            var key = Convert.FromBase64String(v.GetProperty("publicKey").GetString()!);
            Assert.Equal(v.GetProperty("code").GetString(), DeviceCrypto.ShortAuthenticationString(key, v.GetProperty("fingerprint").GetString()!));
        }
    }

    [Fact]
    public void Auth_transcripts_and_der_signatures_match_vectors()
    {
        using var doc = Shared.Load("fixtures/crypto.v1.json");
        foreach (var v in doc.RootElement.GetProperty("authTranscripts").EnumerateArray())
        {
            var transcript = DeviceCrypto.AuthTranscript(
                Guid.Parse(v.GetProperty("pcId").GetString()!), Guid.Parse(v.GetProperty("deviceId").GetString()!),
                Convert.FromBase64String(v.GetProperty("nonce").GetString()!), v.GetProperty("fingerprint").GetString()!);
            Assert.Equal(v.GetProperty("transcript").GetString(), Encoding.UTF8.GetString(transcript));
            Assert.True(DeviceCrypto.VerifySignature(
                Convert.FromBase64String(v.GetProperty("publicKey").GetString()!), transcript,
                Convert.FromBase64String(v.GetProperty("signatureDer").GetString()!)));
        }
    }

    [Fact]
    public void Pairing_links_match_vectors()
    {
        using var doc = Shared.Load("fixtures/pairing-links.v1.json");
        foreach (var v in doc.RootElement.GetProperty("valid").EnumerateArray())
        {
            Assert.True(PairingLink.TryParse(v.GetProperty("link").GetString(), out var link), v.GetProperty("link").GetString());
            Assert.Equal(Guid.Parse(v.GetProperty("pcId").GetString()!), link.PcId);
            Assert.Equal(v.GetProperty("pcName").GetString(), link.PcName);
            Assert.Equal(v.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()!), link.Hosts);
            Assert.Equal(v.GetProperty("port").GetInt32(), link.Port);
            Assert.Equal(v.GetProperty("fingerprint").GetString(), link.CertificateFingerprint);
            Assert.Equal(v.GetProperty("code").GetString(), link.Code);
        }
        foreach (var v in doc.RootElement.GetProperty("invalid").EnumerateArray())
        {
            Assert.False(PairingLink.TryParse(v.GetProperty("link").GetString(), out _), v.GetProperty("why").GetString());
        }
    }
}
