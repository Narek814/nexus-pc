using System.Text.Json;

namespace Nexus.Core.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
    public const int MaxMessageBytes = 64 * 1024;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
}

/// <summary>A structurally valid request (shared/schemas/request.v1.schema.json). Not yet authorized.</summary>
public sealed record RequestEnvelope(
    Guid Id,
    string Type,
    DateTimeOffset Timestamp,
    Guid? PcId,
    bool Confirmed,
    JsonElement Payload);
