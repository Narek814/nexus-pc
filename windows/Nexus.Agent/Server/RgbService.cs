using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Protocol;
using Nexus.RGB;

namespace Nexus.Agent.Server;

/// <summary>
/// Lighting through OpenRGB (P10). A short connection per command, so it keeps working when OpenRGB
/// is started or restarted later. Only devices OpenRGB reports are offered — no hardware claims.
/// Lighting profiles live in rgb.json; a profile with the same id as an app profile (e.g. "gaming")
/// is applied when that app profile runs.
/// </summary>
internal sealed class RgbService(string dataDirectory, int openRgbPort, OpenRgbConnectionGuard guard)
{
    public const int DefaultPort = 6742;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, AllowDuplicateProperties = false };
    private readonly string _path = Path.Combine(dataDirectory, "rgb.json");
    private readonly Lock _lock = new();

    /// <summary>Shown on the phone when another program holds the OpenRGB port (T2, rgb.status state "conflict").</summary>
    public const string ConflictMessage = "Подсветка недоступна: её перехватила другая программа на ПК.";

    public class RgbUnavailableException(string message) : Exception(message);

    /// <summary>The process that accepted the connection is not our OpenRGB; nothing was sent to it.</summary>
    public sealed class RgbConflictException() : RgbUnavailableException(ConflictMessage);

    public async Task<JsonObject> StatusAsync(CancellationToken cancellationToken)
    {
        var result = new JsonObject { ["adapter"] = "OpenRGB", ["profiles"] = ProfilesJson() };
        try
        {
            await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var devices = await client.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            result["available"] = true;
            result["devices"] = new JsonArray([.. devices.Select(d => (JsonNode)new JsonObject
            {
                ["index"] = d.Index,
                ["name"] = d.Name,
                ["type"] = d.Type,
                ["leds"] = d.LedCount,
                ["modes"] = new JsonArray([.. d.Modes.Select(m => (JsonNode)m.Name)]),
            })]);
        }
        catch (RgbUnavailableException ex)
        {
            result["available"] = false;
            result["reason"] = ex.Message;
            if (ex is RgbConflictException)
            {
                result["state"] = "conflict";
            }
            result["devices"] = new JsonArray();
        }
        return result;
    }

    /// <summary>Applies a look to the chosen devices (all if none chosen). Per-device results, never all-or-nothing.</summary>
    public async Task<JsonObject> ApplyAsync(RgbLook look, IReadOnlyList<int>? only, CancellationToken cancellationToken)
    {
        // The protocol layer already validated #RRGGBB; black is only a defensive fallback.
        var color = (RgbColor.TryParse(look.Color, out var baseColor) ? baseColor : RgbColor.Black).Scaled(look.Brightness);
        await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var devices = await client.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        var results = new JsonArray();
        var applied = 0;
        foreach (var device in devices.Where(d => only is null || only.Contains(d.Index)))
        {
            var item = new JsonObject { ["index"] = device.Index, ["name"] = device.Name };
            try
            {
                switch (look.Effect.ToUpperInvariant())
                {
                    case "STATIC":
                        await client.SetStaticAsync(device, color, cancellationToken).ConfigureAwait(false);
                        break;
                    case "OFF":
                        await client.SetStaticAsync(device, RgbColor.Black, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        var mode = device.Modes.FirstOrDefault(m => string.Equals(m.Name, look.Effect, StringComparison.OrdinalIgnoreCase));
                        if (mode is null)
                        {
                            item["ok"] = false;
                            item["error"] = "Это устройство не поддерживает такой эффект.";
                            results.Add(item);
                            continue;
                        }
                        await client.SetModeAsync(device, mode, color, cancellationToken).ConfigureAwait(false);
                        break;
                }
                item["ok"] = true;
                applied++;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
            {
                item["ok"] = false;
                item["error"] = "OpenRGB не ответил.";
            }
            results.Add(item);
        }
        return new JsonObject { ["applied"] = applied, ["results"] = results };
    }

    public IReadOnlyList<RgbProfileDefinition> Profiles
    {
        get
        {
            lock (_lock)
            {
                return Load();
            }
        }
    }

    public void Upsert(RgbProfileDefinition profile)
    {
        lock (_lock)
        {
            var all = Load().Where(p => p.Id != profile.Id).ToList();
            if (all.Count >= 32)
            {
                throw new InvalidOperationException("At most 32 lighting profiles.");
            }
            all.Add(profile);
            Save(all);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var all = Load();
            var remaining = all.Where(p => p.Id != id).ToList();
            if (remaining.Count == all.Count)
            {
                return false;
            }
            Save(remaining);
            return true;
        }
    }

    public RgbProfileDefinition? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id);

    public JsonArray ProfilesJson() =>
        [.. Profiles.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["color"] = p.Look.Color,
            ["brightness"] = p.Look.Brightness,
            ["effect"] = p.Look.Effect,
        })];

    private async Task<OpenRgbClient> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await OpenRgbClient.ConnectAsync("127.0.0.1", openRgbPort, guard.Accept, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenRgbConflictException)
        {
            throw new RgbConflictException();
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RgbUnavailableException("OpenRGB не отвечает. Он запускается вместе с Nexus PC через несколько секунд после включения ПК; если не помогло — перезагрузите ПК.");
        }
    }

    private List<RgbProfileDefinition> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }
        var dtos = JsonSerializer.Deserialize<List<ProfileDto>>(File.ReadAllText(_path), Json) ?? [];
        return [.. dtos.Where(d => Identifiers.IsSlug(d.Id)).Select(d => new RgbProfileDefinition(d.Id, d.Name, new RgbLook(d.Color, Math.Clamp(d.Brightness, 0, 100), d.Effect)))];
    }

    private void Save(List<RgbProfileDefinition> profiles)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles.Select(p => new ProfileDto(p.Id, p.Name, p.Look.Color, p.Look.Brightness, p.Look.Effect)), Json));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record ProfileDto(string Id, string Name, string Color, int Brightness, string Effect);
}
