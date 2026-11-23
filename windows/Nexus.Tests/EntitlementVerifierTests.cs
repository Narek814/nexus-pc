using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Core.Entitlements;

namespace Nexus.Tests;

/// <summary>ENT-2: the agent-side token check against shared/protocol/fixtures/entitlements.v1.json.</summary>
public class EntitlementVerifierTests
{
    internal static JsonDocument Fixture() => Shared.Load("fixtures/entitlements.v1.json");

    internal static EntitlementTrust TestTrust(JsonDocument doc)
    {
        var keys = doc.RootElement.GetProperty("keys").EnumerateArray().Select(k => new EntitlementKey(
            k.GetProperty("kid").GetString()!,
            k.GetProperty("alg").GetString() == "ES256" ? EntitlementAlgorithm.Es256 : EntitlementAlgorithm.Unsupported,
            k.GetProperty("publicKey").ValueKind == JsonValueKind.String ? Convert.FromBase64String(k.GetProperty("publicKey").GetString()!) : null));
        var revoked = doc.RootElement.GetProperty("revokedKids").EnumerateArray().Select(r => r.GetString()!);
        return new EntitlementTrust(keys, revoked, doc.RootElement.GetProperty("context").GetProperty("issuer").GetString()!);
    }

    internal static Guid PcId(JsonDocument doc) => Guid.Parse(doc.RootElement.GetProperty("context").GetProperty("pcId").GetString()!);

    internal static long Now(JsonDocument doc) => doc.RootElement.GetProperty("context").GetProperty("now").GetInt64();

    internal static string Token(JsonDocument doc, string name) =>
        doc.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name).GetProperty("token").GetString()!;

    /// <summary>Signs a payload with the fixture's test-only private key.</summary>
    internal static string Sign(JsonDocument doc, string payloadJson, string kid = "test-es256-a")
    {
        var key = doc.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("kid").GetString() == "test-es256-a");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key.GetProperty("privateKeyTestOnly").GetString()!), out _);
        var segment = B64(Encoding.UTF8.GetBytes(payloadJson));
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(kid + "." + segment), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return kid + "." + segment + "." + B64(signature);
    }

    internal static string Payload(Guid pcId, long iat, long exp, long grace, string plan = "pro", string features = "\"automations\",\"profiles\",\"rgb.sync\",\"monitoring.extended\"") =>
        $"{{\"v\":1,\"typ\":\"nexus-ent+v1\",\"iss\":\"nexus-test-issuer\",\"aud\":\"nexus-agent\",\"sub\":\"s-1\",\"pcId\":\"{pcId:D}\",\"plan\":\"{plan}\",\"features\":[{features}],\"iat\":{iat},\"exp\":{exp},\"graceUntil\":{grace},\"jti\":\"{Guid.NewGuid():D}\"}}";

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Every_fixture_case_gives_its_expected_result()
    {
        using var doc = Fixture();
        var verifier = new EntitlementVerifier(TestTrust(doc));
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 50);
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString();
            var result = verifier.Verify(c.GetProperty("token").GetString(), PcId(doc), Now(doc));
            var expect = c.GetProperty("expect");
            if (expect.GetProperty("ok").GetBoolean())
            {
                Assert.True(result.Ok, $"{name}: {result.Reason}");
            }
            else
            {
                Assert.Equal((name, expect.GetProperty("reason").GetString()), (name, result.Reason));
            }
        }
    }

    [Fact]
    public void Token_of_4097_characters_is_format_before_anything_else()
    {
        using var doc = Fixture();
        var verifier = new EntitlementVerifier(TestTrust(doc));
        var f1 = "test-es256-a." + new string('A', 3997) + "." + new string('A', 86);
        Assert.Equal(4097, f1.Length);
        Assert.Equal(EntitlementReason.Format, verifier.Verify(f1, PcId(doc), Now(doc)).Reason);
        // Even a revoked kid is not looked at: length is first.
        Assert.Equal(EntitlementReason.Format, verifier.Verify("test-es256-revoked." + new string('A', 3992) + "." + new string('A', 86), PcId(doc), Now(doc)).Reason);
    }

    [Fact]
    public void A_signed_token_of_exactly_4096_characters_is_accepted()
    {
        using var doc = Fixture();
        var now = Now(doc);
        var json = Payload(PcId(doc), now - 60, now + 3600, now + 7200);
        // 12 (kid) + 1 + 3996 (2997 payload bytes) + 1 + 86 = 4096. Whitespace is allowed by strict JSON.
        var padded = json.Insert(1, new string(' ', 2997 - Encoding.UTF8.GetByteCount(json)));
        var token = Sign(doc, padded);
        Assert.Equal(4096, token.Length);
        Assert.True(new EntitlementVerifier(TestTrust(doc)).Verify(token, PcId(doc), now).Ok);
        Assert.Equal(EntitlementReason.Format, new EntitlementVerifier(TestTrust(doc)).Verify(token + "A", PcId(doc), now).Reason);
    }

    [Fact]
    public void Kid_is_compared_ordinal_case_sensitive()
    {
        using var doc = Fixture();
        var verifier = new EntitlementVerifier(TestTrust(doc));
        var token = Token(doc, "valid pro token");
        Assert.True(verifier.Verify(token, PcId(doc), Now(doc)).Ok);
        Assert.Equal(EntitlementReason.UnknownKid, verifier.Verify("Test-ES256-A" + token["test-es256-a".Length..], PcId(doc), Now(doc)).Reason);
    }

    [Fact]
    public void Release_table_has_no_test_keys_and_refuses_every_token_as_unknown_kid()
    {
        using var doc = Fixture();
        var testKeys = doc.RootElement.GetProperty("keys").EnumerateArray()
            .Where(k => k.GetProperty("publicKey").ValueKind == JsonValueKind.String)
            .Select(k => k.GetProperty("publicKey").GetString()!).ToList();
        Assert.DoesNotContain(PinnedEntitlementKeys.Keys, k => k.Kid.StartsWith("test-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(PinnedEntitlementKeys.Trust.Keys.Keys, k => k.StartsWith("test-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(PinnedEntitlementKeys.Keys, k => k.SubjectPublicKeyInfo is { } spki && testKeys.Contains(Convert.ToBase64String(spki)));
        Assert.DoesNotContain(PinnedEntitlementKeys.RevokedKids, k => k.StartsWith("test-", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("nexus-test-issuer", PinnedEntitlementKeys.Issuer);
        var verifier = new EntitlementVerifier(PinnedEntitlementKeys.Trust);
        Assert.Equal(EntitlementReason.UnknownKid, verifier.Verify(Token(doc, "valid pro token"), PcId(doc), Now(doc)).Reason);
        Assert.False(PinnedEntitlementKeys.Trust.IsConfigured);
    }

    [Theory]
    [InlineData("profiles.run", null, PlanPolicy.Profiles)]
    [InlineData("rgb.apply", null, PlanPolicy.RgbSync)]
    [InlineData("rgb.profiles.apply", null, PlanPolicy.RgbSync)]
    [InlineData("automations.upsert", null, PlanPolicy.Automations)]
    [InlineData("automations.run", null, PlanPolicy.Automations)]
    [InlineData("metrics.history", "1h", PlanPolicy.MonitoringExtended)]
    [InlineData("metrics.history", "24h", PlanPolicy.MonitoringExtended)]
    [InlineData("metrics.history", "10m", null)]
    [InlineData("automations.list", null, null)]
    [InlineData("rgb.status", null, null)]
    [InlineData("power.shutdown", null, null)]
    [InlineData("input.move", null, null)]
    [InlineData("screen.video", null, null)]
    [InlineData("clipboard.get", null, null)]
    [InlineData("history.list", null, null)]
    [InlineData("files.read", null, null)]
    [InlineData("account.refresh", null, null)]
    [InlineData("auth.prove", null, null)]
    public void Command_to_feature_matrix(string type, string? range, string? feature) =>
        Assert.Equal(feature, PlanPolicy.RequiredFeature(type, range));

    [Fact]
    public void Feature_ids_match_the_protocol_pattern()
    {
        Assert.All(PlanPolicy.ProFeatures.Append(PlanPolicy.Files), f => Assert.True(PlanPolicy.IsFeatureId(f), f));
    }
}
