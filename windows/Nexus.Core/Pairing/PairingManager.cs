using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Core.Pairing;

public enum PairingStatus
{
    Approved,
    Rejected,
    InvalidCode,
    NoActiveSession,
    Busy,
    Expired,
    DeviceLimitReached,
}

public sealed record PairingResult(PairingStatus Status, DeviceRecord? Device = null);

public sealed record PairingTicket(long SessionId, string Code, DateTimeOffset ExpiresAt);

/// <summary>What the PC owner sees before approving: the device name and the verification code.</summary>
public sealed record PendingPairing(string DeviceName, string VerificationCode);

/// <summary>
/// One pairing session at a time, opened on the PC (SECURITY.md §3.2): an 8-digit one-time code,
/// limited lifetime and attempts, and an explicit approve/reject decision on the PC.
/// </summary>
public sealed class PairingManager
{
    public const int MaxCodeAttempts = 5;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private readonly IDeviceStore _devices;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private Session? _active;
    private long _nextSessionId;

    public PairingManager(IDeviceStore devices, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(time);
        _devices = devices;
        _time = time;
    }

    /// <summary>Opens a new session, replacing any previous one.</summary>
    public PairingTicket Start(TimeSpan? lifetime = null)
    {
        var ttl = lifetime ?? DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        Session session;
        lock (_lock)
        {
            EndLocked(_active, PairingStatus.Expired);
            var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            session = new Session(++_nextSessionId, code, _time.GetUtcNow() + ttl, new CancellationTokenSource(ttl, _time));
            _active = session;
            // Registered under the lock so a concurrent Start cannot dispose the source first.
            // System.Threading.Lock is re-entrant, so an immediate callback is safe here.
            session.Expiry.Token.Register(() => End(session, PairingStatus.Expired));
        }
        return new PairingTicket(session.Id, session.Code, session.ExpiresAt);
    }

    /// <summary>Waits until a device submits the correct code. Null if the session ended first.</summary>
    public async Task<PendingPairing?> WaitForRequestAsync(long sessionId, CancellationToken cancellationToken)
    {
        Session? session;
        lock (_lock)
        {
            session = _active?.Id == sessionId ? _active : null;
        }
        if (session is null)
        {
            return null;
        }
        try
        {
            return await session.RequestArrived.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // session expired or was cancelled
        }
    }

    /// <summary>The PC owner's decision. Ends the session either way.</summary>
    public bool Decide(long sessionId, bool approve)
    {
        lock (_lock)
        {
            var session = _active;
            if (session is null || session.Id != sessionId || session.Pending is not { } pending)
            {
                return false;
            }

            if (!approve)
            {
                EndLocked(session, PairingStatus.Rejected);
                return true;
            }

            var device = new DeviceRecord(Guid.NewGuid(), pending.DeviceName, pending.PublicKeySpki, Permissions.DefaultAfterPairing, _time.GetUtcNow());
            try
            {
                _devices.Add(device);
            }
            catch (InvalidOperationException)
            {
                EndLocked(session, PairingStatus.DeviceLimitReached);
                return true;
            }
            pending.Decision.TrySetResult(new PairingResult(PairingStatus.Approved, device));
            EndLocked(session, PairingStatus.Approved);
            return true;
        }
    }

    public void Cancel(long sessionId)
    {
        lock (_lock)
        {
            if (_active?.Id == sessionId)
            {
                EndLocked(_active, PairingStatus.Expired);
            }
        }
    }

    /// <summary>Called for a pair.request. Waits for the PC owner's decision.</summary>
    public async Task<PairingResult> SubmitAsync(string code, string deviceName, byte[] publicKeySpki, string certificateFingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(deviceName);
        ArgumentNullException.ThrowIfNull(publicKeySpki);

        Session session;
        Pending pending;
        lock (_lock)
        {
            if (_active is not { } active)
            {
                return new PairingResult(PairingStatus.NoActiveSession);
            }
            if (active.Pending is not null)
            {
                return new PairingResult(PairingStatus.Busy);
            }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(active.Code)))
            {
                if (++active.FailedAttempts >= MaxCodeAttempts)
                {
                    EndLocked(active, PairingStatus.Expired);
                }
                return new PairingResult(PairingStatus.InvalidCode);
            }

            session = active;
            pending = new Pending(deviceName, publicKeySpki);
            session.Pending = pending;
            session.RequestArrived.TrySetResult(new PendingPairing(deviceName, DeviceCrypto.ShortAuthenticationString(publicKeySpki, certificateFingerprint)));
        }

        try
        {
            return await pending.Decision.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            End(session, PairingStatus.Expired); // the phone went away; never leave an approvable request behind
            throw;
        }
    }

    private void End(Session session, PairingStatus status)
    {
        lock (_lock)
        {
            EndLocked(session, status);
        }
    }

    private void EndLocked(Session? session, PairingStatus status)
    {
        if (session is null || !ReferenceEquals(_active, session))
        {
            return;
        }
        _active = null;
        session.RequestArrived.TrySetCanceled();
        session.Pending?.Decision.TrySetResult(new PairingResult(status));
        session.Expiry.Dispose();
    }

    private sealed class Session(long id, string code, DateTimeOffset expiresAt, CancellationTokenSource expiry)
    {
        public long Id { get; } = id;
        public string Code { get; } = code;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public CancellationTokenSource Expiry { get; } = expiry;
        public int FailedAttempts { get; set; }
        public Pending? Pending { get; set; }
        public TaskCompletionSource<PendingPairing> RequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Pending(string deviceName, byte[] publicKeySpki)
    {
        public string DeviceName { get; } = deviceName;
        public byte[] PublicKeySpki { get; } = publicKeySpki;
        public TaskCompletionSource<PairingResult> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
