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

public sealed record MetricsHistoryPayload(string Range);

public sealed record RgbLook(string Color, int Brightness, string Effect);

public sealed record RgbApplyPayload(RgbLook Look, IReadOnlyList<int>? Devices);

public sealed record RgbProfileDefinition(string Id, string Name, RgbLook Look);

public sealed record RgbProfileUpsertPayload(RgbProfileDefinition Profile);

/// <summary>Any subset of alert settings; ranges are checked by the agent.</summary>
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

    public static PayloadResult MetricsHistory(JsonElement payload)
    {
        if (HasOnly(payload, "range") is { } error)
        {
            return PayloadResult.Invalid(error);
        }
        return TryGetString(payload, "range", out var range) && range is "10m" or "1h" or "24h"
            ? PayloadResult.Ok(new MetricsHistoryPayload(range))
            : PayloadResult.Invalid("'range' must be 10m, 1h or 24h.");
    }

    public static PayloadResult RgbApply(JsonElement payload)
    {
        if (HasOnly(payload, "color", "brightness", "effect", "devices") is { } error)
        {
            return PayloadResult.Invalid(error);
        }
        if (ReadLook(payload) is not { } look)
        {
            return PayloadResult.Invalid("'color' must be #RRGGBB, 'brightness' 0..100, 'effect' 1..64 characters.");
        }
        List<int>? devices = null;
        if (payload.TryGetProperty("devices", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 32)
            {
                return PayloadResult.Invalid("'devices' must be a list of device indexes.");
            }
            devices = [];
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index) || index is < 0 or > 63)
                {
                    return PayloadResult.Invalid("'devices' must be a list of device indexes.");
                }
                devices.Add(index);
            }
        }
        return PayloadResult.Ok(new RgbApplyPayload(look, devices));
    }

    public static PayloadResult RgbProfileUpsert(JsonElement payload)
    {
        if (HasOnly(payload, "profile") is { } error)
        {
            return PayloadResult.Invalid(error);
        }
        if (!payload.TryGetProperty("profile", out var p) || p.ValueKind != JsonValueKind.Object || HasOnly(p, "id", "name", "color", "brightness", "effect") is not null)
        {
            return PayloadResult.Invalid("'profile' must have id, name, color, brightness and effect.");
        }
        if (!TryGetSlug(p, "id", out var id) || !TryGetString(p, "name", out var name) || !Identifiers.IsValidDisplayName(name) || ReadLook(p) is not { } look)
        {
            return PayloadResult.Invalid("Invalid lighting profile.");
        }
        return PayloadResult.Ok(new RgbProfileUpsertPayload(new RgbProfileDefinition(id, name, look)));
    }

    private static RgbLook? ReadLook(JsonElement obj)
    {
        if (!TryGetString(obj, "color", out var color) || color.Length != 7 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit)
            || !obj.TryGetProperty("brightness", out var b) || b.ValueKind != JsonValueKind.Number || !b.TryGetInt32(out var brightness) || brightness is < 0 or > 100
            || !TryGetString(obj, "effect", out var effect) || !Identifiers.IsValidDisplayName(effect))
        {
            return null;
        }
        return new RgbLook(color.ToUpperInvariant(), brightness, effect);
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
