using System.Text.Json;
using Nexus.Core.Apps;
using Nexus.Core.Pairing;
using Nexus.Core.Security;

namespace Nexus.Core.Protocol;

public sealed record EmptyPayload
{
    public static EmptyPayload Instance { get; } = new();
}

public sealed record PowerPayload(int DelaySeconds, bool Force)
{
    public const int MaxDelaySeconds = 86_400;
}

public sealed record AppRefPayload(string AppId);

public sealed record ProfileRefPayload(string ProfileId);

public sealed record ProfileUpsertPayload(ProfileDefinition Profile);

public sealed record PairRequestPayload(string Code, string DeviceName, byte[] PublicKeySpki);

public sealed record AuthBeginPayload(Guid DeviceId);

public sealed record AuthProvePayload(byte[] Signature);

public sealed record PayloadResult(object? Value, string? Error)
{
    public static PayloadResult Ok(object value) => new(value, null);
    public static PayloadResult Invalid(string error) => new(null, error);
}

/// <summary>Strict readers for each payload shape in the schema's $defs.</summary>
public static class PayloadParsers
{
    public static PayloadResult Empty(JsonElement payload) =>
        HasOnly(payload) is { } error ? PayloadResult.Invalid(error) : PayloadResult.Ok(EmptyPayload.Instance);

    public static PayloadResult Power(JsonElement payload)
    {
        if (HasOnly(payload, "delaySeconds", "force") is { } error)
        {
            return PayloadResult.Invalid(error);
        }

        var delay = 0;
        if (payload.TryGetProperty("delaySeconds", out var delayElement))
        {
            if (delayElement.ValueKind != JsonValueKind.Number || !delayElement.TryGetInt32(out delay)
                || delay < 0 || delay > PowerPayload.MaxDelaySeconds)
            {
                return PayloadResult.Invalid($"'delaySeconds' must be an integer 0..{PowerPayload.MaxDelaySeconds}.");
            }
        }

        var force = false;
        if (payload.TryGetProperty("force", out var forceElement))
        {
            if (forceElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return PayloadResult.Invalid("'force' must be a boolean.");
            }
            force = forceElement.GetBoolean();
        }

        return PayloadResult.Ok(new PowerPayload(delay, force));
    }

    public static PayloadResult AppRef(JsonElement payload) =>
        HasOnly(payload, "appId") is { } error ? PayloadResult.Invalid(error)
        : TryGetSlug(payload, "appId", out var id) ? PayloadResult.Ok(new AppRefPayload(id))
        : PayloadResult.Invalid("'appId' must be a slug (a-z, 0-9, '-').");

    public static PayloadResult ProfileRef(JsonElement payload) =>
        HasOnly(payload, "profileId") is { } error ? PayloadResult.Invalid(error)
        : TryGetSlug(payload, "profileId", out var id) ? PayloadResult.Ok(new ProfileRefPayload(id))
        : PayloadResult.Invalid("'profileId' must be a slug (a-z, 0-9, '-').");

    public static PayloadResult ProfileUpsert(JsonElement payload)
    {
        if (HasOnly(payload, "profile") is { } error)
        {
            return PayloadResult.Invalid(error);
        }
        if (!payload.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
        {
            return PayloadResult.Invalid("'profile' must be an object.");
        }
        if (HasOnly(profile, "id", "name", "appIds") is { } profileError)
        {
            return PayloadResult.Invalid(profileError);
        }
        if (!TryGetSlug(profile, "id", out var id))
        {
            return PayloadResult.Invalid("'profile.id' must be a slug.");
        }
        if (!profile.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
            || !ProfileRules.IsValidName(nameElement.GetString()))
        {
            return PayloadResult.Invalid($"'profile.name' must be 1..{ProfileRules.MaxNameLength} visible characters.");
        }
        if (!profile.TryGetProperty("appIds", out var appIdsElement) || appIdsElement.ValueKind != JsonValueKind.Array)
        {
            return PayloadResult.Invalid("'profile.appIds' must be an array.");
        }
        if (appIdsElement.GetArrayLength() > ProfileRules.MaxApps)
        {
            return PayloadResult.Invalid($"A profile can contain at most {ProfileRules.MaxApps} apps.");
        }

        var appIds = new List<string>();
        foreach (var item in appIdsElement.EnumerateArray())
        {
            var appId = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!Identifiers.IsSlug(appId))
            {
                return PayloadResult.Invalid("Every app id must be a slug.");
            }
            if (appIds.Contains(appId!, StringComparer.Ordinal))
            {
                return PayloadResult.Invalid($"App '{appId}' is listed twice.");
            }
            appIds.Add(appId!);
        }

        return PayloadResult.Ok(new ProfileUpsertPayload(new ProfileDefinition(id, nameElement.GetString()!, appIds)));
    }

    public static PayloadResult PairRequest(JsonElement payload)
    {
        if (HasOnly(payload, "code", "deviceName", "publicKey") is { } error)
        {
            return PayloadResult.Invalid(error);
        }
        if (!TryGetString(payload, "code", out var code) || !PairingLink.IsValidCode(code))
        {
            return PayloadResult.Invalid("'code' must be 8 digits.");
        }
        if (!TryGetString(payload, "deviceName", out var name) || !Identifiers.IsValidDisplayName(name))
        {
            return PayloadResult.Invalid("'deviceName' must be 1..64 visible characters.");
        }
        if (!TryGetBase64(payload, "publicKey", DeviceCrypto.MaxSpkiBytes, out var key) || !DeviceCrypto.IsValidP256PublicKey(key))
        {
            return PayloadResult.Invalid("'publicKey' must be a base64 P-256 SubjectPublicKeyInfo.");
        }
        return PayloadResult.Ok(new PairRequestPayload(code, name, key));
    }

    public static PayloadResult AuthBegin(JsonElement payload)
    {
        if (HasOnly(payload, "deviceId") is { } error)
        {
            return PayloadResult.Invalid(error);
        }
        return TryGetString(payload, "deviceId", out var text) && Guid.TryParseExact(text, "D", out var id)
            ? PayloadResult.Ok(new AuthBeginPayload(id))
            : PayloadResult.Invalid("'deviceId' must be a UUID.");
    }

    public static PayloadResult AuthProve(JsonElement payload)
    {
        if (HasOnly(payload, "signature") is { } error)
        {
            return PayloadResult.Invalid(error);
        }
        return TryGetBase64(payload, "signature", DeviceCrypto.MaxSignatureBytes, out var signature)
            ? PayloadResult.Ok(new AuthProvePayload(signature))
            : PayloadResult.Invalid("'signature' must be base64, at most 128 bytes.");
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (obj.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }
        return false;
    }

    private static bool TryGetBase64(JsonElement obj, string name, int maxBytes, out byte[] value)
    {
        value = [];
        if (!TryGetString(obj, name, out var text) || text.Length == 0 || text.Length > (maxBytes + 2) / 3 * 4)
        {
            return false;
        }
        var buffer = new byte[maxBytes];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written == 0)
        {
            return false;
        }
        value = buffer[..written];
        return true;
    }

    private static string? HasOnly(JsonElement obj, params string[] allowed)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                return $"Unexpected payload field '{property.Name}'.";
            }
        }
        return null;
    }

    private static bool TryGetSlug(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (obj.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            && Identifiers.IsSlug(element.GetString()))
        {
            value = element.GetString()!;
            return true;
        }
        return false;
    }
}
