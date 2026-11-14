namespace Nexus.Core.Security;

/// <summary>
/// Rejects a message id that a device has already used while that message could still pass the
/// timestamp check. An entry is kept until <c>expiresAt</c> inclusive, which the caller sets to the
/// last instant the message's timestamp is acceptable (ts + max skew), so no replay window remains.
/// </summary>
public sealed class ReplayGuard
{
    private readonly Dictionary<(string DeviceId, Guid MessageId), DateTimeOffset> _seen = [];
    private readonly Lock _lock = new();
    private readonly int _capacity;

    public ReplayGuard(int capacity = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public ReplayResult TryRegister(string deviceId, Guid messageId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        lock (_lock)
        {
            if (_seen.TryGetValue((deviceId, messageId), out var expiry) && expiry >= now)
            {
                return ReplayResult.Replayed;
            }
            if (_seen.Count >= _capacity)
            {
                Prune(now);
                if (_seen.Count >= _capacity)
                {
                    return ReplayResult.CapacityExceeded;
                }
            }
            _seen[(deviceId, messageId)] = expiresAt;
            return ReplayResult.Accepted;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _seen.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
        {
            _seen.Remove(key);
        }
    }
}

public enum ReplayResult
{
    Accepted,
    Replayed,
    CapacityExceeded,
}
