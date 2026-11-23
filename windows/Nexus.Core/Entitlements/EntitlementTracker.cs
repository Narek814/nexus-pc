namespace Nexus.Core.Entitlements;

public enum EntitlementState
{
    None,
    Active,
    Grace,
    Expired,
    Revoked,
}

/// <summary>What hello / account.refresh report and what the plan checks use.</summary>
public sealed record EntitlementStatus(EntitlementState State, string Plan, IReadOnlyList<string> Features, DateTimeOffset? ValidUntil)
{
    public static readonly EntitlementStatus Free = new(EntitlementState.None, "free", [], null);

    public bool Has(string feature) => Features.Contains(feature, StringComparer.Ordinal);

    public string WireState => State switch
    {
        EntitlementState.Active => "active",
        EntitlementState.Grace => "grace",
        EntitlementState.Expired => "expired",
        EntitlementState.Revoked => "revoked",
        _ => "none",
    };
}

/// <summary>Persisted entitlement state (entitlement.json in the protected data folder). Unix seconds.</summary>
public sealed record EntitlementRecord
{
    public int V { get; init; } = 1;
    /// <summary>The last token that passed every check; re-verified (signature, schema, issuer, pcId, terms) on load.</summary>
    public string? Token { get; init; }
    /// <summary>Highest iat ever accepted: an older (still validly signed) token is refused.</summary>
    public long MaxIat { get; init; }
    /// <summary>Latest wall-clock time seen; saved every few minutes. Clock set back past it → grace forfeited until contact.</summary>
    public long LastSeen { get; init; }
    /// <summary>Service uptime accumulated since the token was accepted (monotonic, survives restarts by being saved).</summary>
    public long UptimeMs { get; init; }
    /// <summary>The clock was set back: grace counts as spent until a token with a newer iat arrives.</summary>
    public bool ClockRolledBack { get; init; }
    /// <summary>The server reported revocation: Free at once, until a token with an iat newer than <see cref="MaxIat"/>.</summary>
    public bool Revoked { get; init; }
    /// <summary>UTC day (Unix seconds / 86400) of <see cref="FileBytes"/>.</summary>
    public long FileDay { get; init; }
    /// <summary>File transfer bytes counted on <see cref="FileDay"/> (plan.limit for Free).</summary>
    public long FileBytes { get; init; }
}

public interface IEntitlementStore
{
    /// <summary>Null when there is no trustworthy file (missing, foreign access entries, unreadable).</summary>
    EntitlementRecord? Load();

    void Save(EntitlementRecord record);
}

public interface IEntitlementClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic time since an arbitrary start; never goes back, unaffected by clock changes.</summary>
    TimeSpan Monotonic { get; }
}

public sealed class SystemEntitlementClock(TimeProvider time) : IEntitlementClock
{
    private readonly long _start = time.GetTimestamp();

    public DateTimeOffset UtcNow => time.GetUtcNow();

    public TimeSpan Monotonic => time.GetElapsedTime(_start);
}

/// <summary>
/// Entitlement state with clock-rollback protection (SECURITY.md «Аккаунты и права», E5/A6). Tokens enter only
/// through <see cref="Accept"/> (internal API for tests and the future server fetch, E4); there is no external path.
/// </summary>
public sealed class EntitlementTracker
{
    /// <summary>Wall clock earlier than the last seen time by more than this → set back.</summary>
    public const long RollbackToleranceSeconds = 300;
    public const string StaleReason = "stale";
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(10);

    private readonly EntitlementVerifier _verifier;
    private readonly Guid _pcId;
    private readonly IEntitlementStore _store;
    private readonly IEntitlementClock _clock;
    private readonly Lock _lock = new();
    private EntitlementRecord _record;
    private EntitlementToken? _token;
    private TimeSpan _lastMonotonic;
    private TimeSpan _lastSaved;
    private bool _dirty;

    public EntitlementTracker(EntitlementVerifier verifier, Guid pcId, IEntitlementStore store, IEntitlementClock clock)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pcId = pcId;
        _lastMonotonic = _lastSaved = clock.Monotonic;
        var wall = clock.UtcNow.ToUnixTimeSeconds();
        _record = store.Load() ?? new EntitlementRecord { LastSeen = wall };
        if (_record.Token is { } stored)
        {
            // Re-checked with this build's keys and revoked kids; times are judged by the state below.
            var check = verifier.Verify(stored, pcId, wall, checkTime: false);
            _token = check.Token;
            if (_token is null || _token.Iat < _record.MaxIat)
            {
                _token = null;
                _record = _record with { Token = null, UptimeMs = 0 };
                _dirty = true;
            }
        }
        lock (_lock)
        {
            Advance();
        }
    }

    public bool Enforced => _verifier.Trust.IsConfigured;

    public EntitlementStatus Current
    {
        get
        {
            lock (_lock)
            {
                Advance();
                return Compute();
            }
        }
    }

    /// <summary>Periodic: accumulates uptime, detects clock rollback, saves at most every <see cref="SaveInterval"/>.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            Advance();
        }
    }

    /// <summary>
    /// Takes a token (internal API). Rejected tokens change nothing. Accepted only with iat ≥ max(iat) seen; after a
    /// revocation or a clock rollback only a token with a newer iat lifts the restriction.
    /// </summary>
    public EntitlementVerification Accept(string token)
    {
        lock (_lock)
        {
            Advance();
            var wall = _clock.UtcNow.ToUnixTimeSeconds();
            var check = _verifier.Verify(token, _pcId, wall);
            if (check.Token is not { } t)
            {
                return check;
            }
            if (t.Iat < _record.MaxIat || (_record.Revoked && t.Iat <= _record.MaxIat))
            {
                return new EntitlementVerification(null, StaleReason);
            }
            if (t.Iat > _record.MaxIat)
            {
                // Fresh from the server: re-anchors the clock and the uptime budget.
                _record = _record with
                {
                    Token = t.Raw, MaxIat = t.Iat, LastSeen = Math.Max(wall, t.Iat), UptimeMs = 0,
                    ClockRolledBack = false, Revoked = false,
                };
            }
            else
            {
                _record = _record with { Token = t.Raw, LastSeen = Math.Max(_record.LastSeen, wall) };
            }
            _token = t;
            Save();
            return check;
        }
    }

    /// <summary>The server reported the entitlement revoked: Free at once, no grace, until a newer token.</summary>
    public void MarkRevoked()
    {
        lock (_lock)
        {
            Advance();
            _record = _record with { Revoked = true, Token = null, UptimeMs = 0, MaxIat = Math.Max(_record.MaxIat, _token?.Iat ?? 0) };
            _token = null;
            Save();
        }
    }

    /// <summary>
    /// Counts file transfer bytes for today (UTC). Refuses only when the plan has no <see cref="PlanPolicy.FilesUnlimited"/>
    /// and a limit is set; <paramref name="limit"/> null = no limit (the current default).
    /// </summary>
    public bool TryUseFileBytes(long bytes, long? limit, out long effectiveLimit)
    {
        lock (_lock)
        {
            Advance();
            var day = _clock.UtcNow.ToUnixTimeSeconds() / 86_400;
            var used = _record.FileDay == day ? _record.FileBytes : 0;
            var unlimited = Enforced && Compute().Has(PlanPolicy.FilesUnlimited);
            effectiveLimit = limit ?? 0;
            if (!unlimited && limit is { } max && used + bytes > max)
            {
                return false;
            }
            _record = _record with { FileDay = day, FileBytes = used + Math.Max(0, bytes) };
            _dirty = true;
            return true;
        }
    }

    internal EntitlementRecord Record
    {
        get
        {
            lock (_lock)
            {
                return _record;
            }
        }
    }

    /// <summary>Saves now (service stopping).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            Advance();
            Save();
        }
    }

    private void Advance()
    {
        var monotonic = _clock.Monotonic;
        var delta = monotonic - _lastMonotonic;
        _lastMonotonic = monotonic;
        if (_token is not null && delta > TimeSpan.Zero)
        {
            _record = _record with { UptimeMs = _record.UptimeMs + (long)delta.TotalMilliseconds };
            _dirty = true;
        }
        var wall = _clock.UtcNow.ToUnixTimeSeconds();
        if (wall < _record.LastSeen - RollbackToleranceSeconds)
        {
            if (!_record.ClockRolledBack)
            {
                _record = _record with { ClockRolledBack = true };
                Save(); // remembered at once: a restart must not forget it
                return;
            }
        }
        else if (wall > _record.LastSeen)
        {
            _record = _record with { LastSeen = wall };
            _dirty = true;
        }
        if (_dirty && monotonic - _lastSaved >= SaveInterval)
        {
            Save();
        }
    }

    private void Save()
    {
        _store.Save(_record);
        _lastSaved = _lastMonotonic;
        _dirty = false;
    }

    private EntitlementStatus Compute()
    {
        if (_record.Revoked)
        {
            return new EntitlementStatus(EntitlementState.Revoked, "free", [], null);
        }
        if (_token is not { } t)
        {
            return EntitlementStatus.Free;
        }
        // Lower bound of the real time: wall clock, the latest time seen, and iat plus the service uptime since then.
        var now = Math.Max(Math.Max(_clock.UtcNow.ToUnixTimeSeconds(), _record.LastSeen), t.Iat + (_record.UptimeMs / 1000));
        if (now <= t.Exp)
        {
            return new EntitlementStatus(EntitlementState.Active, t.Plan, t.Features, DateTimeOffset.FromUnixTimeSeconds(t.Exp));
        }
        if (!_record.ClockRolledBack && now <= t.GraceUntil)
        {
            return new EntitlementStatus(EntitlementState.Grace, t.Plan, t.Features, DateTimeOffset.FromUnixTimeSeconds(t.GraceUntil));
        }
        return new EntitlementStatus(EntitlementState.Expired, "free", [], DateTimeOffset.FromUnixTimeSeconds(t.GraceUntil));
    }
}
