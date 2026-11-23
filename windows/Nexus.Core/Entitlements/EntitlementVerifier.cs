using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexus.Core.Entitlements;

/// <summary>First failing check (docs/API.md «Токен прав»); the values equal the fixtures' "reason".</summary>
public static class EntitlementReason
{
    public const string Format = "format";
    public const string RevokedKid = "revokedKid";
    public const string UnknownKid = "unknownKid";
    public const string Algorithm = "algorithm";
    public const string Signature = "signature";
    public const string Json = "json";
    public const string Schema = "schema";
    public const string Issuer = "issuer";
    public const string PcId = "pcId";
    public const string Lifetime = "lifetime";
    public const string Grace = "grace";
    public const string NotYetValid = "notYetValid";
    public const string Expired = "expired";
}

/// <summary>A token that passed every check. <c>sub</c> is deliberately not kept: it never reaches clients.</summary>
public sealed record EntitlementToken(
    string Raw, string Kid, string Plan, IReadOnlyList<string> Features, long Iat, long Exp, long GraceUntil, Guid Jti);

public sealed record EntitlementVerification(EntitlementToken? Token, string? Reason)
{
    public bool Ok => Token is not null;
}

/// <summary>
/// Verifies entitlement tokens strictly in the documented order: format → revokedKid → unknownKid → algorithm →
/// signature → json → schema → issuer → pcId → lifetime → grace → notYetValid → expired. The payload is not
/// parsed before the signature is verified.
/// </summary>
public sealed class EntitlementVerifier(EntitlementTrust trust)
{
    public const int MaxTokenLength = 4096;
    public const long MaxLifetimeSeconds = 86_400;
    public const long MaxGraceSeconds = 604_800;
    public const long MaxClockSkewSeconds = 300;
    public const long MinUnixSeconds = 1_700_000_000;
    public const long MaxUnixSeconds = 4_102_444_800;
    private const int SignatureBytes = 64;
    private const int SignatureChars = 86;

    private static readonly string[] Fields =
        ["v", "typ", "iss", "aud", "sub", "pcId", "plan", "features", "iat", "exp", "graceUntil", "jti"];

    public EntitlementTrust Trust { get; } = trust ?? throw new ArgumentNullException(nameof(trust));

    /// <param name="checkTime">False only to re-check a stored token: notYetValid and expired are then skipped (the tracker decides the state).</param>
    public EntitlementVerification Verify(string? token, Guid pcId, long nowUnixSeconds, bool checkTime = true)
    {
        // format: length first, before anything else is looked at.
        if (token is null || token.Length > MaxTokenLength)
        {
            return Fail(EntitlementReason.Format);
        }
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0) || !IsKid(parts[0]) || parts[2].Length != SignatureChars
            || !TryBase64Url(parts[1], out var payload) || !TryBase64Url(parts[2], out var signature) || signature.Length != SignatureBytes)
        {
            return Fail(EntitlementReason.Format);
        }
        var kid = parts[0];
        if (Trust.RevokedKids.Contains(kid))
        {
            return Fail(EntitlementReason.RevokedKid);
        }
        if (!Trust.Keys.TryGetValue(kid, out var key))
        {
            return Fail(EntitlementReason.UnknownKid);
        }
        if (key.Algorithm != EntitlementAlgorithm.Es256 || key.SubjectPublicKeyInfo is not { Length: > 0 } spki)
        {
            return Fail(EntitlementReason.Algorithm);
        }
        if (!VerifyEs256(spki, Encoding.ASCII.GetBytes(kid + "." + parts[1]), signature))
        {
            return Fail(EntitlementReason.Signature);
        }
        if (!IsStrictJson(payload))
        {
            return Fail(EntitlementReason.Json);
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return Fail(EntitlementReason.Json);
        }
        using (document)
        {
            if (ReadSchema(document.RootElement) is not { } p)
            {
                return Fail(EntitlementReason.Schema);
            }
            if (!string.Equals(p.Iss, Trust.Issuer, StringComparison.Ordinal))
            {
                return Fail(EntitlementReason.Issuer);
            }
            if (!string.Equals(p.PcId, pcId.ToString("D"), StringComparison.Ordinal))
            {
                return Fail(EntitlementReason.PcId);
            }
            if (p.Exp - p.Iat is <= 0 or > MaxLifetimeSeconds)
            {
                return Fail(EntitlementReason.Lifetime);
            }
            if (p.GraceUntil - p.Exp is < 0 or > MaxGraceSeconds)
            {
                return Fail(EntitlementReason.Grace);
            }
            if (checkTime && p.Iat > nowUnixSeconds + MaxClockSkewSeconds)
            {
                return Fail(EntitlementReason.NotYetValid);
            }
            if (checkTime && nowUnixSeconds > p.GraceUntil)
            {
                return Fail(EntitlementReason.Expired);
            }
            return new EntitlementVerification(new EntitlementToken(token, kid, p.Plan, p.Features, p.Iat, p.Exp, p.GraceUntil, p.Jti), null);
        }
    }

    private static EntitlementVerification Fail(string reason) => new(null, reason);

    private static bool VerifyEs256(byte[] spki, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || ecdsa.KeySize != 256)
            {
                return false;
            }
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal static bool IsKid(string s) =>
        s.Length is >= 1 and <= 64 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    /// <summary>Strict base64url: only [A-Za-z0-9_-], no padding, length not ≡ 1 (mod 4), zero trailing bits.</summary>
    internal static bool TryBase64Url(string s, out byte[] bytes)
    {
        bytes = [];
        if (s.Length == 0 || s.Length % 4 == 1 || !s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            return false;
        }
        var b = s.Replace('-', '+').Replace('_', '/');
        try
        {
            bytes = Convert.FromBase64String(b.PadRight(b.Length + ((4 - (b.Length % 4)) % 4), '='));
        }
        catch (FormatException)
        {
            return false;
        }
        // Re-encoding must give the same text: rejects non-zero trailing bits.
        return string.Equals(Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), s, StringComparison.Ordinal);
    }

    /// <summary>
    /// No duplicate keys at any depth and numbers only as decimal integer literals -?[0-9]+ without leading zeros
    /// and without -0 (by the raw bytes). Invalid UTF-8, comments and trailing commas are refused by the reader.
    /// </summary>
    internal static bool IsStrictJson(byte[] utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
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
                        if (!IsIntegerLiteral(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan))
                        {
                            return false;
                        }
                        break;
                }
            }
            return reader.BytesConsumed == utf8.Length || utf8.AsSpan((int)reader.BytesConsumed).Trim(" \t\r\n"u8).IsEmpty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsIntegerLiteral(ReadOnlySpan<byte> raw)
    {
        var negative = raw.Length > 0 && raw[0] == (byte)'-';
        var digits = negative ? raw[1..] : raw;
        if (digits.Length == 0 || (digits.Length > 1 && digits[0] == (byte)'0') || (negative && digits is [(byte)'0']))
        {
            return false;
        }
        foreach (var d in digits)
        {
            if (d is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }
        return true;
    }

    private sealed record Payload(string Iss, string PcId, string Plan, IReadOnlyList<string> Features, long Iat, long Exp, long GraceUntil, Guid Jti);

    /// <summary>shared/schemas/entitlement.v1.schema.json, by hand (no schema library in the agent).</summary>
    private static Payload? ReadSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != Fields.Length || names.Any(n => !Fields.Contains(n, StringComparer.Ordinal)))
        {
            return null;
        }
        if (!Int(root, "v", out var v) || v != 1
            || !Str(root, "typ", out var typ) || typ != "nexus-ent+v1"
            || !Str(root, "iss", out var iss) || !IsPrintable(iss)
            || !Str(root, "aud", out var aud) || aud != "nexus-agent"
            || !Str(root, "sub", out var sub) || !IsPrintable(sub)
            || !Str(root, "pcId", out var pc) || !IsUuid(pc)
            || !Str(root, "plan", out var plan) || plan is not ("free" or "pro")
            || !Time(root, "iat", out var iat) || !Time(root, "exp", out var exp) || !Time(root, "graceUntil", out var grace)
            || !Str(root, "jti", out var jti) || !IsUuid(jti))
        {
            return null;
        }
        var list = root.GetProperty("features");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 64)
        {
            return null;
        }
        var features = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } f || !PlanPolicy.IsFeatureId(f) || features.Contains(f, StringComparer.Ordinal))
            {
                return null;
            }
            features.Add(f);
        }
        return new Payload(iss, pc, plan, features, iat, exp, grace, Guid.ParseExact(jti, "D"));
    }

    private static bool Str(JsonElement o, string name, out string value)
    {
        value = "";
        if (o.GetProperty(name) is not { ValueKind: JsonValueKind.String } e)
        {
            return false;
        }
        value = e.GetString()!;
        return true;
    }

    private static bool Int(JsonElement o, string name, out long value)
    {
        value = 0;
        return o.GetProperty(name) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out value);
    }

    private static bool Time(JsonElement o, string name, out long value) =>
        Int(o, name, out value) && value is >= MinUnixSeconds and <= MaxUnixSeconds;

    private static bool IsPrintable(string s) => s.Length is >= 1 and <= 128 && s.All(c => c is >= '\x21' and <= '\x7e');

    private static bool IsUuid(string s) =>
        s.Length == 36 && s.Select((c, i) => i is 8 or 13 or 18 or 23 ? c == '-' : c is (>= '0' and <= '9') or (>= 'a' and <= 'f')).All(x => x);
}
