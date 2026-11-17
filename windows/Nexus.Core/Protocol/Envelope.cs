using System.Text.Json;

namespace Nexus.Core.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
    /// <summary>Largest message from an authenticated device (file chunks: 512 KiB as base64 plus envelope).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Before authentication nothing needs more than a few hundred bytes.</summary>
    public const int MaxAnonymousMessageBytes = 64 * 1024;
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
