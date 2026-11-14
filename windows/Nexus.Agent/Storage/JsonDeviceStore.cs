using System.Text.Json;
using Nexus.Core.Security;

namespace Nexus.Agent.Storage;

/// <summary>Paired devices in devices.json inside the protected data directory.</summary>
internal sealed class JsonDeviceStore : IDeviceStore
{
    public const int MaxDevices = 32;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, AllowDuplicateProperties = false };

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<DeviceRecord> _devices;

    public JsonDeviceStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "devices.json");
        _devices = File.Exists(_path) ? Load(_path) : [];
    }

    public IReadOnlyList<DeviceRecord> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _devices];
            }
        }
    }

    public DeviceRecord? Find(Guid deviceId)
    {
        lock (_lock)
        {
            return _devices.Find(d => d.DeviceId == deviceId);
        }
    }

    public void Add(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lock)
        {
            if (_devices.Count >= MaxDevices)
            {
                throw new InvalidOperationException($"At most {MaxDevices} devices can be paired.");
            }
            Commit([.. _devices, device]);
        }
    }

    public bool Remove(Guid deviceId)
    {
        lock (_lock)
        {
            var remaining = _devices.Where(d => d.DeviceId != deviceId).ToList();
            if (remaining.Count == _devices.Count)
            {
                return false;
            }
            Commit(remaining);
            return true;
        }
    }

    public bool SetPermissions(Guid deviceId, Permission permissions)
    {
        lock (_lock)
        {
            var index = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0)
            {
                return false;
            }
            var updated = _devices.ToList();
            updated[index] = updated[index] with { Permissions = permissions };
            Commit(updated);
            return true;
        }
    }

    // Write first, then swap the in-memory list, so memory never claims something the disk does not have.
    private void Commit(List<DeviceRecord> devices)
    {
        var dto = devices.Select(d => new DeviceDto(d.DeviceId, d.Name, Convert.ToBase64String(d.PublicKeySpki), [.. Permissions.ToWireNames(d.Permissions)], d.PairedAt));
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(dto, Json));
        _devices = devices;
    }

    private static List<DeviceRecord> Load(string path)
    {
        var dtos = JsonSerializer.Deserialize<List<DeviceDto>>(File.ReadAllText(path), Json) ?? [];
        return [.. dtos.Select(d =>
        {
            var key = Convert.FromBase64String(d.PublicKey);
            if (!DeviceCrypto.IsValidP256PublicKey(key) || !Permissions.TryParse(d.Permissions, out var permissions))
            {
                throw new InvalidDataException($"{path}: device {d.DeviceId} is corrupt.");
            }
            return new DeviceRecord(d.DeviceId, d.Name, key, permissions, d.PairedAt);
        })];
    }

    private sealed record DeviceDto(Guid DeviceId, string Name, string PublicKey, string[] Permissions, DateTimeOffset PairedAt);
}
