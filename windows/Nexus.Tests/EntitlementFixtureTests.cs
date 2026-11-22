using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Tests;

/// <summary>
/// ENT-1 contract: shared/protocol/fixtures/entitlements.v1.json and the plan fields of the response
/// fixtures. Checks that the fixtures are honest (format, signatures over kid "." payload, strict JSON,
/// field sets, time relations) so ENT-2 and NexusKit can rely on them. The agent-side token check is
/// ENT-2; the helpers below are test-only references for the fixture contract.
/// </summary>
public class EntitlementFixtureTests
{
    private static readonly string[] Required =
        ["v", "typ", "iss", "aud", "sub", "pcId", "plan", "features", "iat", "exp", "graceUntil", "jti"];

    private static readonly string[] KeySelectionOrSignature = ["revokedKid", "unknownKid", "algorithm", "signature"];

    private static JsonDocument Fixture() => Shared.Load("fixtures/entitlements.v1.json");

    private static JsonElement Case(JsonDocument doc, string name) =>
        doc.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string? Reason(JsonElement c) =>
        c.GetProperty("expect").TryGetProperty("reason", out var r) ? r.GetString() : null;

    /// <summary>Strict base64url: only [A-Za-z0-9_-], no padding, zero trailing bits.</summary>
    internal static bool TryBase64Url(string s, out byte[] bytes)
    {
        bytes = [];
        if (s.Length == 0 || s.Length % 4 == 1 || !s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            return false;
        }
        var b = s.Replace('-', '+').Replace('_', '/');
        bytes = Convert.FromBase64String(b.PadRight(b.Length + ((4 - (b.Length % 4)) % 4), '='));
        // Re-encoding must give the same text: rejects non-zero trailing bits.
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == s;
    }

    private static bool IsKid(string s) =>
        s.Length is >= 1 and <= 64 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    /// <summary>kid "." payload "." signature with strict encoding and a 64-byte signature.</summary>
    internal static bool TryFormat(string token, out string kid, out string payloadSegment, out byte[] payload, out byte[] signature)
    {
        kid = payloadSegment = "";
        payload = signature = [];
        var parts = token.Split('.');
        if (parts.Length != 3 || !IsKid(parts[0]) || parts[2].Length != 86)
        {
            return false;
        }
        kid = parts[0];
        payloadSegment = parts[1];
        return TryBase64Url(parts[1], out payload) && TryBase64Url(parts[2], out signature) && signature.Length == 64;
    }

    /// <summary>
    /// Strict JSON: no duplicate keys at any depth, numbers only as decimal integers (no fraction,
    /// exponent or -0). Same rule as Python object_pairs_hook + parse_float/parse_int hooks.
    /// </summary>
    internal static bool IsStrictJson(byte[] utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            var scopes = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        scopes.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (!scopes.Peek()!.Add(reader.GetString()!))
                        {
                            return false;
                        }
                        break;
                    case JsonTokenType.Number:
                        var raw = reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray();
                        var digits = raw[0] == (byte)'-' ? raw[1..] : raw;
                        if (digits.Length == 0 || !digits.All(d => d is >= (byte)'0' and <= (byte)'9') || (raw[0] == (byte)'-' && digits is [(byte)'0']))
                        {
                            return false;
                        }
                        break;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool VerifiesWithTestKey(JsonDocument doc, string token)
    {
        if (!TryFormat(token, out var kid, out var segment, out _, out var signature))
        {
            return false;
        }
        var key = doc.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("alg").GetString() == "ES256");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.GetProperty("publicKey").GetString()!), out _);
        // The signature covers kid "." payload-segment, so the kid cannot be swapped.
        return ecdsa.VerifyData(Encoding.ASCII.GetBytes(kid + "." + segment), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static TheoryData<string> CaseNames()
    {
        using var doc = Fixture();
        var data = new TheoryData<string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_case_fails_exactly_at_its_stated_stage(string name)
    {
        using var doc = Fixture();
        var c = Case(doc, name);
        var token = c.GetProperty("token").GetString()!;
        var reason = Reason(c);
        var schemaValid = c.GetProperty("schemaValid").GetBoolean();
        Assert.Equal(reason is null, c.GetProperty("expect").GetProperty("ok").GetBoolean());

        var formatOk = TryFormat(token, out var kid, out _, out var payload, out _);
        if (reason == "format")
        {
            Assert.False(formatOk);
            Assert.False(schemaValid);
            return;
        }
        Assert.True(formatOk, "only format cases may break the encoding");

        // Key selection and signature cases must not verify; every later stage must be genuinely signed.
        var verifies = VerifiesWithTestKey(doc, token);
        if (reason is not null && KeySelectionOrSignature.Contains(reason))
        {
            Assert.False(verifies && reason == "signature");
            if (reason == "unknownKid")
            {
                Assert.DoesNotContain(doc.RootElement.GetProperty("keys").EnumerateArray(), k => k.GetProperty("kid").GetString() == kid);
            }
            if (reason == "revokedKid")
            {
                Assert.Contains(doc.RootElement.GetProperty("revokedKids").EnumerateArray(), k => k.GetString() == kid);
            }
            if (reason == "algorithm")
            {
                Assert.NotEqual("ES256", doc.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("kid").GetString() == kid).GetProperty("alg").GetString());
            }
            return;
        }
        Assert.True(verifies, "cases after the signature stage must carry a genuine signature");

        var strict = IsStrictJson(payload);
        if (reason == "json")
        {
            Assert.False(strict);
            Assert.False(schemaValid);
            return;
        }
        Assert.True(strict);

        using var json = JsonDocument.Parse(payload);
        var names = json.RootElement.ValueKind == JsonValueKind.Object
            ? json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()
            : [];
        if (reason == "schema")
        {
            Assert.False(schemaValid);
            return;
        }
        Assert.True(schemaValid);
        Assert.Equal(Required.Order(StringComparer.Ordinal), names);
        Assert.Equal("nexus-ent+v1", json.RootElement.GetProperty("typ").GetString());
        Assert.Equal("nexus-agent", json.RootElement.GetProperty("aud").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public void Positive_cases_use_the_test_kid_and_respect_time_limits()
    {
        using var doc = Fixture();
        var ctx = doc.RootElement.GetProperty("context");
        var now = ctx.GetProperty("now").GetInt64();
        var positives = doc.RootElement.GetProperty("cases").EnumerateArray().Where(c => Reason(c) is null).ToList();
        Assert.NotEmpty(positives);
        foreach (var c in positives)
        {
            Assert.True(TryFormat(c.GetProperty("token").GetString()!, out var kid, out _, out var payload, out _));
            Assert.Equal("test-es256-a", kid);
            using var p = JsonDocument.Parse(payload);
            var r = p.RootElement;
            long iat = r.GetProperty("iat").GetInt64(), exp = r.GetProperty("exp").GetInt64(), grace = r.GetProperty("graceUntil").GetInt64();
            Assert.InRange(exp - iat, 1, 86400);
            Assert.InRange(grace - exp, 0, 604800);
            Assert.True(iat <= now + 300);
            Assert.True(grace > now);
            Assert.Equal(ctx.GetProperty("pcId").GetString(), r.GetProperty("pcId").GetString());
            Assert.Equal(ctx.GetProperty("issuer").GetString(), r.GetProperty("iss").GetString());
        }
    }

    [Theory]
    [InlineData("wrong signature")]
    [InlineData("signed by a key that is not pinned for kid")]
    [InlineData("signature over payload only (kid not covered)")]
    public void Negative_signature_cases_do_not_verify(string name)
    {
        using var doc = Fixture();
        Assert.False(VerifiesWithTestKey(doc, Case(doc, name).GetProperty("token").GetString()!));
    }

    [Fact]
    public void Kid_is_bound_by_the_signature()
    {
        using var doc = Fixture();
        var token = Case(doc, "valid pro token").GetProperty("token").GetString()!;
        Assert.True(VerifiesWithTestKey(doc, token));
        var parts = token.Split('.');
        Assert.False(VerifiesWithTestKey(doc, "test-es256-b." + parts[1] + "." + parts[2]));
    }

    [Theory]
    [InlineData("""{"a":1}""", true)]
    [InlineData("""{"a":[{"b":1},{"b":2}]}""", true)]
    [InlineData("""{"a":-5}""", true)]
    [InlineData("""{"a":1,"a":1}""", false)]
    [InlineData("""{"a":{"b":1,"b":2}}""", false)]
    [InlineData("""{"a":[{"x":{"c":1,"c":1}}]}""", false)]
    [InlineData("""{"a":1e0}""", false)]
    [InlineData("""{"a":1.0}""", false)]
    [InlineData("""{"a":1.5}""", false)]
    [InlineData("""{"a":-0}""", false)]
    [InlineData("""{"a":1E5}""", false)]
    public void Strict_json_reference_rejects_duplicates_and_non_integers(string json, bool expected)
    {
        Assert.Equal(expected, IsStrictJson(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Dotnet_json_document_also_rejects_duplicates_when_configured()
    {
        // .NET 10: JsonDocumentOptions.AllowDuplicateProperties = false rejects duplicates at any depth.
        var options = new JsonDocumentOptions { AllowDuplicateProperties = false };
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("""{"a":{"b":1,"b":2}}""", options));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("""{"a":1,"a":1}""", options));
        using var ok = JsonDocument.Parse("""{"a":{"b":1}}""", options);
    }

    [Theory]
    [InlineData("AAAA", true)]
    [InlineData("AAE", true)]
    [InlineData("AAF", false)]
    [InlineData("AA==", false)]
    [InlineData("A+/A", false)]
    [InlineData("AA A", false)]
    [InlineData("A", false)]
    [InlineData("", false)]
    public void Strict_base64url_reference(string text, bool expected)
    {
        Assert.Equal(expected, TryBase64Url(text, out _));
    }

    [Fact]
    public void Required_negative_cases_are_present()
    {
        using var doc = Fixture();
        var reasons = doc.RootElement.GetProperty("cases").EnumerateArray()
            .Select(Reason).Where(r => r is not null).ToHashSet(StringComparer.Ordinal);
        string[] needed = ["format", "revokedKid", "unknownKid", "algorithm", "signature", "json", "schema", "issuer", "pcId", "lifetime", "grace", "notYetValid", "expired"];
        Assert.All(needed, r => Assert.Contains(r, reasons));
        var names = doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToHashSet();
        Assert.All(
            ["foreign aud", "signature 63 bytes", "signature 65 bytes", "duplicate key in a nested object", "number with exponent",
             "integer written as x.0", "non-zero trailing bits in signature base64url", "extra field (kid inside payload)"],
            n => Assert.Contains(n, names));
    }

    [Fact]
    public void Test_private_key_is_marked_and_matches_the_test_public_key()
    {
        using var doc = Fixture();
        Assert.Contains("TEST KEYS ONLY", doc.RootElement.GetProperty("WARNING").GetString(), StringComparison.Ordinal);
        Assert.All(doc.RootElement.GetProperty("keys").EnumerateArray(), k => Assert.StartsWith("test-", k.GetProperty("kid").GetString(), StringComparison.Ordinal));
        var key = doc.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("alg").GetString() == "ES256");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key.GetProperty("privateKeyTestOnly").GetString()!), out _);
        Assert.Equal(key.GetProperty("publicKey").GetString(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Test_key_is_not_embedded_in_product_code()
    {
        using var doc = Fixture();
        var pub = doc.RootElement.GetProperty("keys")[0].GetProperty("publicKey").GetString()!;
        var core = typeof(CommandCatalog).Assembly;
        var constants = core.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue());
        Assert.DoesNotContain(pub, constants);
    }

    [Fact]
    public void Account_refresh_is_read_needs_login_and_no_device_permission()
    {
        Assert.True(CommandCatalog.V1.TryGet("account.refresh", out var spec));
        Assert.True(spec.RequiresAuth);
        Assert.Equal(Permission.None, spec.RequiredPermission);
        Assert.Equal(RiskClass.Read, spec.Risk);
    }

    [Fact]
    public void Plan_error_codes_are_stable()
    {
        Assert.Equal("plan.required", ErrorCodes.PlanRequired);
        Assert.Equal("plan.limit", ErrorCodes.PlanLimit);
        Assert.Equal("rate.limited", ErrorCodes.RateLimited);
    }

    [Fact]
    public void Every_error_code_in_valid_response_fixtures_is_a_known_code()
    {
        var known = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        using var doc = Shared.Load("fixtures/responses.v1.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("valid").GetBoolean()))
        {
            if (c.GetProperty("message").TryGetProperty("error", out var error))
            {
                Assert.Contains(error.GetProperty("code").GetString()!, known);
            }
        }
    }

    [Fact]
    public void Valid_responses_never_carry_account_identity_and_hello_before_login_has_no_plan()
    {
        using var doc = Shared.Load("fixtures/responses.v1.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("valid").GetBoolean()))
        {
            var raw = c.GetProperty("message").GetRawText();
            Assert.DoesNotContain("\"email\"", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\"accountId\"", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\"sub\"", raw, StringComparison.Ordinal);
            if (c.GetProperty("message").TryGetProperty("payload", out var p)
                && p.TryGetProperty("authenticated", out var auth) && !auth.GetBoolean())
            {
                Assert.False(p.TryGetProperty("plan", out _));
                Assert.False(p.TryGetProperty("features", out _));
                Assert.False(p.TryGetProperty("entitlementState", out _));
                Assert.False(p.TryGetProperty("validUntil", out _));
            }
        }
    }

    [Fact]
    public void Auth_and_pair_schemas_have_no_account_fields()
    {
        using var schema = Shared.Load("schemas/request.v1.schema.json");
        var raw = schema.RootElement.GetRawText();
        foreach (var field in new[] { "\"email\"", "\"accountId\"", "\"entitlement\"", "\"plan\"", "\"sub\"" })
        {
            Assert.DoesNotContain(field, raw, StringComparison.Ordinal);
        }
    }
}
