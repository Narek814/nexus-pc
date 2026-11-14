namespace Nexus.Core.Security;

/// <summary>A paired device. The public key is not secret, but the record's integrity is (SECURITY.md §3.3).</summary>
public sealed record DeviceRecord(
    Guid DeviceId,
    string Name,
    byte[] PublicKeySpki,
    Permission Permissions,
    DateTimeOffset PairedAt);

public interface IDeviceStore
{
    IReadOnlyList<DeviceRecord> All { get; }

    DeviceRecord? Find(Guid deviceId);

    /// <exception cref="InvalidOperationException">The device limit is reached.</exception>
    void Add(DeviceRecord device);

    bool Remove(Guid deviceId);

    bool SetPermissions(Guid deviceId, Permission permissions);
}
