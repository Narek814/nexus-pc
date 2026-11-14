using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Core.Protocol;

/// <summary>
/// Strict parser for request envelopes. Rejects anything the schema would reject:
/// unknown fields, duplicate keys, wrong types, oversized input.
/// </summary>
public static partial class EnvelopeParser
{
    private static readonly HashSet<string> AllowedFields = ["v", "id", "kind", "type", "ts", "pcId", "confirmed", "payload"];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 16,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static ParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > ProtocolInfo.MaxMessageBytes)
        {
            return Fail(ErrorCodes.TooLarge, $"Message exceeds {ProtocolInfo.MaxMessageBytes} bytes.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException)
        {
            return Fail(ErrorCodes.Malformed, "Message is not valid JSON.");
        }

        using (document)
        {
            return ParseRoot(document.RootElement);
        }
    }

    private static ParseResult ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail(ErrorCodes.Malformed, "Message must be a JSON object.");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name))
            {
                return Fail(ErrorCodes.Malformed, $"Unknown field '{property.Name}'.");
            }
        }

        // Version first, so a future client gets a precise error instead of "malformed".
        if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
        {
            return Fail(ErrorCodes.Malformed, "Field 'v' must be an integer.");
        }
        if (version != ProtocolInfo.Version)
        {
            return Fail(ErrorCodes.UnsupportedVersion, $"Protocol version {version} is not supported.");
        }

        if (!TryGetString(root, "id", out var idText) || !Guid.TryParseExact(idText, "D", out var id))
        {
            return Fail(ErrorCodes.Malformed, "Field 'id' must be a UUID.");
        }
        if (!TryGetString(root, "kind", out var kind) || kind != "request")
        {
            return Fail(ErrorCodes.Malformed, "Field 'kind' must be 'request'.");
        }
        if (!TryGetString(root, "type", out var type))
        {
            return Fail(ErrorCodes.Malformed, "Field 'type' must be a string.");
        }
        if (!TryGetString(root, "ts", out var tsText) || !TryParseTimestamp(tsText, out var ts))
        {
            return Fail(ErrorCodes.Malformed, "Field 'ts' must be an RFC 3339 timestamp.");
        }

        Guid? pcId = null;
        if (root.TryGetProperty("pcId", out var pcIdElement))
        {
            if (pcIdElement.ValueKind != JsonValueKind.String || !Guid.TryParseExact(pcIdElement.GetString(), "D", out var parsedPcId))
            {
                return Fail(ErrorCodes.Malformed, "Field 'pcId' must be a UUID.");
            }
            pcId = parsedPcId;
        }

        var confirmed = false;
        if (root.TryGetProperty("confirmed", out var confirmedElement))
        {
            if (confirmedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Fail(ErrorCodes.Malformed, "Field 'confirmed' must be a boolean.");
            }
            confirmed = confirmedElement.GetBoolean();
        }

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return Fail(ErrorCodes.Malformed, "Field 'payload' must be an object.");
        }

        return new ParseResult(new RequestEnvelope(id, type, ts, pcId, confirmed, payload.Clone()), null);
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryParseTimestamp(string text, out DateTimeOffset value)
    {
        value = default;
        return Rfc3339().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    private static ParseResult Fail(string code, string message) => new(null, new ProtocolError(code, message));

    // Must match the `ts` pattern in shared/schemas/request.v1.schema.json. \z: no trailing newline.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339();
}

public sealed record ParseResult(RequestEnvelope? Envelope, ProtocolError? Error)
{
    public bool IsSuccess => Envelope is not null;
}
