namespace Nexus.Core.Security;

/// <summary>Who is sending a message, as established by the transport/auth layer.</summary>
public sealed record ClientContext(bool IsAuthenticated, string? DeviceId, Permission Permissions)
{
    public static ClientContext Anonymous { get; } = new(false, null, Permission.None);

    public static ClientContext ForDevice(string deviceId, Permission permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return new ClientContext(true, deviceId, permissions);
    }
}
