using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Agent.Storage;

internal sealed record AgentConfig
{
    public const int DefaultPort = 47800;

    public required Guid PcId { get; init; }
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
