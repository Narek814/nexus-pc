using Nexus.Core.Entitlements;

namespace Nexus.Tests;

internal sealed class FakeEntitlementClock(long unixSeconds) : IEntitlementClock
{
    public long Wall { get; set; } = unixSeconds;
    public TimeSpan Mono { get; set; }

    public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeSeconds(Wall);
    public TimeSpan Monotonic => Mono;

    /// <summary>Real time passes: wall and monotonic together.</summary>
    public void Pass(long seconds)
    {
        Wall += seconds;
        Mono += TimeSpan.FromSeconds(seconds);
    }
}

internal sealed class MemoryEntitlementStore : IEntitlementStore
{
    public EntitlementRecord? Saved { get; set; }
    public int Saves { get; private set; }

    public EntitlementRecord? Load() => Saved;

    public void Save(EntitlementRecord record)
    {
        Saved = record;
        Saves++;
    }
}

/// <summary>E5: clock rollback, stale tokens, revocation, grace budget.</summary>
public class EntitlementTrackerTests
{
    private static (EntitlementTracker Tracker, FakeEntitlementClock Clock, MemoryEntitlementStore Store, Guid PcId, System.Text.Json.JsonDocument Doc) Create(long? now = null, MemoryEntitlementStore? store = null)
    {
        var doc = EntitlementVerifierTests.Fixture();
        var clock = new FakeEntitlementClock(now ?? EntitlementVerifierTests.Now(doc));
        store ??= new MemoryEntitlementStore();
        var pcId = EntitlementVerifierTests.PcId(doc);
        return (new EntitlementTracker(new EntitlementVerifier(EntitlementVerifierTests.TestTrust(doc)), pcId, store, clock), clock, store, pcId, doc);
    }

    private static string Token(System.Text.Json.JsonDocument doc, Guid pcId, long iat, long life = 86_400, long grace = 604_800) =>
        EntitlementVerifierTests.Sign(doc, EntitlementVerifierTests.Payload(pcId, iat, iat + life, iat + life + grace));

    [Fact]
    public void No_token_is_free_none()
    {
        var (t, _, _, _, doc) = Create();
        using var _d = doc;
        Assert.Equal(EntitlementState.None, t.Current.State);
        Assert.Equal("free", t.Current.Plan);
        Assert.Empty(t.Current.Features);
    }

    [Fact]
    public void Active_then_grace_then_expired()
    {
        var (t, clock, _, pc, doc) = Create();
        using var _d = doc;
        Assert.True(t.Accept(Token(doc, pc, clock.Wall)).Ok);
        Assert.Equal(EntitlementState.Active, t.Current.State);
        Assert.True(t.Current.Has(PlanPolicy.Automations));
        clock.Pass(86_401);
        Assert.Equal(EntitlementState.Grace, t.Current.State);
        Assert.True(t.Current.Has(PlanPolicy.Automations));
        clock.Pass(604_800);
        Assert.Equal(EntitlementState.Expired, t.Current.State);
        Assert.Equal("free", t.Current.Plan);
        Assert.False(t.Current.Has(PlanPolicy.Automations));
    }

    [Fact]
    public void Clock_set_back_forfeits_grace_until_a_newer_token()
    {
        var (t, clock, store, pc, doc) = Create();
        using var _d = doc;
        var iat = clock.Wall;
        Assert.True(t.Accept(Token(doc, pc, iat)).Ok);
        clock.Pass(86_400 + 3600); // in grace
        Assert.Equal(EntitlementState.Grace, t.Current.State);
        clock.Wall -= 86_400; // set back a day: wall says "active" again
        Assert.Equal(EntitlementState.Expired, t.Current.State);
        Assert.True(store.Saved!.ClockRolledBack); // saved at once

        // Restart keeps it.
        var again = new EntitlementTracker(new EntitlementVerifier(EntitlementVerifierTests.TestTrust(doc)), pc, store, clock);
        Assert.Equal(EntitlementState.Expired, again.Current.State);

        // A token with the same iat does not lift it; a newer one does.
        clock.Wall += 86_400;
        Assert.True(again.Accept(Token(doc, pc, clock.Wall)).Ok);
        Assert.Equal(EntitlementState.Active, again.Current.State);
    }

    [Fact]
    public void Clock_held_back_does_not_extend_the_term_uptime_budget()
    {
        var (t, clock, _, pc, doc) = Create();
        using var _d = doc;
        Assert.True(t.Accept(Token(doc, pc, clock.Wall, grace: 0)).Ok);
        // The service runs 25 h, but the wall clock is kept still (moved back a little every few minutes).
        clock.Mono += TimeSpan.FromHours(25);
        Assert.Equal(EntitlementState.Expired, t.Current.State);
    }

    [Fact]
    public void Clock_moved_forward_ends_grace_early()
    {
        var (t, clock, _, pc, doc) = Create();
        using var _d = doc;
        Assert.True(t.Accept(Token(doc, pc, clock.Wall)).Ok);
        clock.Wall += 9 * 86_400;
        Assert.Equal(EntitlementState.Expired, t.Current.State);
        clock.Wall -= 9 * 86_400; // back again: still not active (latest seen time and rollback)
        Assert.Equal(EntitlementState.Expired, t.Current.State);
    }

    [Fact]
    public void Older_signed_token_is_refused_and_changes_nothing()
    {
        var (t, clock, store, pc, doc) = Create();
        using var _d = doc;
        var old = Token(doc, pc, clock.Wall - 3600);
        var fresh = Token(doc, pc, clock.Wall);
        Assert.True(t.Accept(fresh).Ok);
        var before = store.Saved;
        Assert.Equal(EntitlementTracker.StaleReason, t.Accept(old).Reason);
        Assert.Equal(before!.MaxIat, t.Record.MaxIat);
        Assert.Equal(fresh, t.Record.Token);
    }

    [Fact]
    public void Rejected_token_does_not_touch_max_iat_or_last_seen()
    {
        var (t, clock, _, pc, doc) = Create();
        using var _d = doc;
        var before = t.Record;
        var wrongPc = EntitlementVerifierTests.Sign(doc, EntitlementVerifierTests.Payload(Guid.NewGuid(), clock.Wall + 100, clock.Wall + 200, clock.Wall + 300));
        Assert.Equal(EntitlementReason.PcId, t.Accept(wrongPc).Reason);
        Assert.Equal(before.MaxIat, t.Record.MaxIat);
        Assert.Equal(before.LastSeen, t.Record.LastSeen);
        Assert.Equal(EntitlementState.None, t.Current.State);
        _ = pc;
    }

    [Fact]
    public void Revoked_is_free_at_once_without_grace_until_a_newer_token()
    {
        var (t, clock, store, pc, doc) = Create();
        using var _d = doc;
        var token = Token(doc, pc, clock.Wall);
        Assert.True(t.Accept(token).Ok);
        t.MarkRevoked();
        Assert.Equal(EntitlementState.Revoked, t.Current.State);
        Assert.Null(t.Current.ValidUntil);
        Assert.False(t.Current.Has(PlanPolicy.Automations));
        Assert.Equal(EntitlementTracker.StaleReason, t.Accept(token).Reason); // same iat: no
        var restarted = new EntitlementTracker(new EntitlementVerifier(EntitlementVerifierTests.TestTrust(doc)), pc, store, clock);
        Assert.Equal(EntitlementState.Revoked, restarted.Current.State);
        clock.Pass(60);
        Assert.True(restarted.Accept(Token(doc, pc, clock.Wall)).Ok);
        Assert.Equal(EntitlementState.Active, restarted.Current.State);
    }

    [Fact]
    public void Stored_token_is_reverified_on_load_and_dropped_if_it_fails()
    {
        var (t, clock, store, pc, doc) = Create();
        using var _d = doc;
        Assert.True(t.Accept(Token(doc, pc, clock.Wall)).Ok);
        var empty = new EntitlementTrust([], [], "nexus-test-issuer");
        var reloaded = new EntitlementTracker(new EntitlementVerifier(empty), pc, store, clock);
        Assert.Equal(EntitlementState.None, reloaded.Current.State);
    }

    [Fact]
    public void Last_seen_is_saved_periodically_not_on_every_tick()
    {
        var (t, clock, store, _, doc) = Create();
        using var _d = doc;
        var saves = store.Saves;
        clock.Pass(60);
        t.Tick();
        Assert.Equal(saves, store.Saves);
        clock.Pass((long)EntitlementTracker.SaveInterval.TotalSeconds);
        t.Tick();
        Assert.True(store.Saves > saves);
        Assert.Equal(clock.Wall, store.Saved!.LastSeen);
    }

    [Fact]
    public void File_bytes_are_counted_and_never_refused_without_a_limit()
    {
        var (t, _, _, _, doc) = Create();
        using var _d = doc;
        Assert.True(t.TryUseFileBytes(1_000_000, PlanPolicy.FreeFileBytesPerDay, out _));
        Assert.Equal(1_000_000, t.Record.FileBytes);
        Assert.False(t.TryUseFileBytes(10, 1_000_005, out var limit));
        Assert.Equal(1_000_005, limit);
    }
}
