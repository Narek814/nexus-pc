using Nexus.Core.Security;

namespace Nexus.Tests;

internal sealed class InMemoryDeviceStore(int limit = 32) : IDeviceStore
{
    private readonly List<DeviceRecord> _devices = [];

    public IReadOnlyList<DeviceRecord> All => [.. _devices];

    public DeviceRecord? Find(Guid deviceId) => _devices.Find(d => d.DeviceId == deviceId);

    public void Add(DeviceRecord device)
    {
        if (_devices.Count >= limit)
        {
            throw new InvalidOperationException("limit");
        }
        _devices.Add(device);
    }

    public bool Remove(Guid deviceId) => _devices.RemoveAll(d => d.DeviceId == deviceId) > 0;

    public bool SetPermissions(Guid deviceId, Permission permissions)
    {
        var index = _devices.FindIndex(d => d.DeviceId == deviceId);
        if (index < 0)
        {
            return false;
        }
        _devices[index] = _devices[index] with { Permissions = permissions };
        return true;
    }
}
