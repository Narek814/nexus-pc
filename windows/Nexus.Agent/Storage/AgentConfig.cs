using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Agent.Storage;

internal sealed record AgentConfig
{
    public const int DefaultPort = 47800;

    public required Guid PcId { get; init; }
    /// <summary>The random pcId of an installation before E4 (kept for reference; not accepted at login).</summary>
    public Guid? LegacyPcId { get; init; }
    public int Port { get; init; } = DefaultPort;
    public string? CertificateThumbprint { get; init; }
    /// <summary>The only executable allowed on the session pipe. Default: ..\Tray\Nexus.Tray.exe next to the agent.</summary>
    public string? TrayPath { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowDuplicateProperties = false,
    };

    /// <summary>What <see cref="LoadOrCreate(string, Guid, out PcIdChange)"/> changed about the pcId.</summary>
    public enum PcIdChange { None, Created, MigratedFromLegacy, KeyReplaced }

    /// <summary>
    /// Loads config.json with PcId = <paramref name="derivedPcId"/> (from the device key). A pre-E4 random id
    /// moves to LegacyPcId; a different derived id means the key was lost and recreated. Saved atomically when changed.
    /// </summary>
    public static AgentConfig LoadOrCreate(string dataDirectory, Guid derivedPcId, out PcIdChange change)
    {
        var existed = File.Exists(Path.Combine(dataDirectory, "config.json"));
        var config = existed ? LoadOrCreate(dataDirectory) : new AgentConfig { PcId = derivedPcId };
        change = existed ? PcIdChange.None : PcIdChange.Created;
        if (existed && config.PcId != derivedPcId)
        {
            if (Nexus.Core.Security.PcIdDerivation.IsDerived(config.PcId))
            {
                change = PcIdChange.KeyReplaced;
                config = config with { PcId = derivedPcId };
            }
            else
            {
                change = PcIdChange.MigratedFromLegacy;
                config = config with { PcId = derivedPcId, LegacyPcId = config.LegacyPcId ?? config.PcId };
            }
        }
        if (change != PcIdChange.None)
        {
            config.Save(dataDirectory);
        }
        return config;
    }

    public static AgentConfig LoadOrCreate(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "config.json");
        if (File.Exists(path))
        {
            var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path} is empty.");
            if (config.Port is < 1 or > 65535)
            {
                throw new InvalidDataException($"{path}: port must be 1..65535.");
            }
            return config;
        }
        var created = new AgentConfig { PcId = Guid.NewGuid() };
        created.Save(dataDirectory);
        return created;
    }

    public void Save(string dataDirectory) =>
        AtomicFile.WriteAllText(Path.Combine(dataDirectory, "config.json"), JsonSerializer.Serialize(this, Json));
}
