using System.Security.Cryptography;
using System.Text.Json;

namespace Nexus.Notifications;

/// <summary>Thresholds and delivery. Editable from the phone (alerts.set); stored in the agent's data directory.</summary>
public sealed record AlertSettings
{
    public bool GpuTemperatureEnabled { get; init; } = true;
    public int GpuTemperatureC { get; init; } = 85;
    public bool CpuUsageEnabled { get; init; } = true;
    public int CpuUsagePercent { get; init; } = 95;
    public bool MemoryUsageEnabled { get; init; } = true;
    public int MemoryUsagePercent { get; init; } = 90;
    public bool DiskFreeEnabled { get; init; } = true;
    public int DiskFreePercent { get; init; } = 10;
    public bool PowerEventsEnabled { get; init; } = true;

    /// <summary>Same alert again at most this often.</summary>
    public int CooldownMinutes { get; init; } = 30;

    /// <summary>Off by default: notifications leave the PC through a third-party service (D11).</summary>
    public bool NtfyEnabled { get; init; }
    public string NtfyServer { get; init; } = "https://ntfy.sh";
    /// <summary>Secret, random channel name. Whoever knows it can read the notifications.</summary>
    public string? NtfyTopic { get; init; }

    public static readonly (int Min, int Max) GpuTemperatureRange = (50, 110);
    public static readonly (int Min, int Max) PercentRange = (50, 100);
    public static readonly (int Min, int Max) DiskFreeRange = (1, 50);
    public static readonly (int Min, int Max) CooldownRange = (5, 1440);

    /// <summary>Null if valid, otherwise what is wrong.</summary>
    public string? Validate()
    {
        static bool In(int v, (int Min, int Max) r) => v >= r.Min && v <= r.Max;
        if (!In(GpuTemperatureC, GpuTemperatureRange)) return $"GPU temperature threshold must be {GpuTemperatureRange.Min}..{GpuTemperatureRange.Max} °C.";
        if (!In(CpuUsagePercent, PercentRange) || !In(MemoryUsagePercent, PercentRange)) return $"Usage thresholds must be {PercentRange.Min}..{PercentRange.Max} %.";
        if (!In(DiskFreePercent, DiskFreeRange)) return $"Disk free threshold must be {DiskFreeRange.Min}..{DiskFreeRange.Max} %.";
        if (!In(CooldownMinutes, CooldownRange)) return $"Cooldown must be {CooldownRange.Min}..{CooldownRange.Max} minutes.";
        if (!Uri.TryCreate(NtfyServer, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps || server.AbsolutePath.Length > 1 || server.Query.Length > 0)
        {
            return "ntfy server must be an https:// address without a path.";
        }
        return null;
    }

    /// <summary>"nexuspc-" + 128 random bits: unguessable.</summary>
    public static string NewTopic() => "nexuspc-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

public static class AlertSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, AllowDuplicateProperties = false };

    public static AlertSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AlertSettings();
        }
        var settings = JsonSerializer.Deserialize<AlertSettings>(File.ReadAllText(path), Json) ?? new AlertSettings();
        return settings.Validate() is null ? settings : new AlertSettings { NtfyTopic = settings.NtfyTopic };
    }

    public static void Save(string path, AlertSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, path, overwrite: true);
    }
}
