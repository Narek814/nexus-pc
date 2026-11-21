using Nexus.Core.Storage;

namespace Nexus.Tests;

/// <summary>
/// The in-process gate in front of the protected lock (CI #82: a second attempt from the holding process got
/// error 52 and was reported Unavailable instead of Busy).
/// </summary>
public sealed class InProcessLockGateTests
{
    /// <summary>Stands for the OS lock: counts calls, and like the private namespace refuses a second opener.</summary>
    private sealed class FakeOsLock
    {
        public int Calls;
        public int Released;
        public bool Open;
        public LockOutcome? Forced;
        public string? ForcedFailure;
        public bool ThrowOnRelease;

        public LockOutcome Acquire(out IDisposable? held, out string? failure)
        {
            Interlocked.Increment(ref Calls);
            held = null;
            failure = ForcedFailure;
            if (Forced is { } forced)
            {
                return forced;
            }
            if (Open)
            {
                failure = "cannot open the private namespace: (error 52)";
                return LockOutcome.Unavailable;
            }
            Open = true;
            held = new Handle(this);
            return LockOutcome.Acquired;
        }

        private sealed class Handle(FakeOsLock owner) : IDisposable
        {
            public void Dispose()
            {
                owner.Released++;
                if (owner.ThrowOnRelease)
                {
                    throw new InvalidOperationException("release failed");
                }
                owner.Open = false;
            }
        }
    }

    [Fact]
    public void Second_attempt_while_held_is_busy_without_touching_the_os_lock_and_acquires_after_release()
    {
        var os = new FakeOsLock();
        var gate = new InProcessLockGate();
        Assert.Equal(LockOutcome.Acquired, gate.TryAcquire(os.Acquire, out var first, out var failure));
        Assert.Null(failure);
        Assert.NotNull(first);
        Assert.True(gate.Held);

        Assert.Equal(LockOutcome.Busy, gate.TryAcquire(os.Acquire, out var second, out var secondFailure));
        Assert.Null(second);
        Assert.Null(secondFailure);
        Assert.Equal(1, os.Calls);

        first!.Dispose();
        Assert.False(gate.Held);
        Assert.False(os.Open);
        Assert.Equal(LockOutcome.Acquired, gate.TryAcquire(os.Acquire, out var again, out _));
        again!.Dispose();
        Assert.Equal(2, os.Calls);
    }

    [Fact]
    public void Dispose_is_idempotent_and_does_not_release_a_later_holder()
    {
        var os = new FakeOsLock();
        var gate = new InProcessLockGate();
        Assert.Equal(LockOutcome.Acquired, gate.TryAcquire(os.Acquire, out var first, out _));
        first!.Dispose();
        Assert.Equal(LockOutcome.Acquired, gate.TryAcquire(os.Acquire, out var second, out _));
        first.Dispose(); // a stale second Dispose must not clear the new holder
        Assert.True(gate.Held);
        Assert.Equal(1, os.Released);
        Assert.Equal(LockOutcome.Busy, gate.TryAcquire(os.Acquire, out _, out _));
        second!.Dispose();
        Assert.False(gate.Held);
        Assert.Equal(2, os.Released);
    }

    [Theory]
    [InlineData(LockOutcome.Busy, null)]
    [InlineData(LockOutcome.Unavailable, "cannot open the private namespace: (error 52)")]
    [InlineData(LockOutcome.Unavailable, "cannot open the private namespace: Access is denied. (error 5)")]
    public void Outcomes_of_the_os_lock_pass_through_unchanged_and_do_not_mark_the_process(LockOutcome forced, string? text)
    {
        // Error 52 that does not come from this process holding the lock stays Unavailable (fail closed).
        var os = new FakeOsLock { Forced = forced, ForcedFailure = text };
        var gate = new InProcessLockGate();
        Assert.Equal(forced, gate.TryAcquire(os.Acquire, out var held, out var failure));
        Assert.Null(held);
        Assert.Equal(text, failure);
        Assert.False(gate.Held);
        Assert.Equal(forced, gate.TryAcquire(os.Acquire, out _, out _));
        Assert.Equal(2, os.Calls);
    }

    [Fact]
    public void A_failed_release_still_clears_the_mark_so_the_next_attempt_reaches_the_os_lock()
    {
        var os = new FakeOsLock { ThrowOnRelease = true };
        var gate = new InProcessLockGate();
        Assert.Equal(LockOutcome.Acquired, gate.TryAcquire(os.Acquire, out var held, out _));
        Assert.Throws<InvalidOperationException>(() => held!.Dispose());
        Assert.False(gate.Held);
        // The OS lock is still open: the next attempt is refused there (Unavailable), not by a stale mark.
        Assert.Equal(LockOutcome.Unavailable, gate.TryAcquire(os.Acquire, out _, out var failure));
        Assert.Contains("error 52", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Concurrent_attempts_from_one_process_give_one_acquired_and_the_rest_busy()
    {
        var os = new FakeOsLock();
        var gate = new InProcessLockGate();
        const int threads = 16;
        var outcomes = new LockOutcome[threads];
        var holders = new IDisposable?[threads];
        using var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            outcomes[i] = gate.TryAcquire(os.Acquire, out holders[i], out _);
        })).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());

        Assert.Equal(1, outcomes.Count(o => o == LockOutcome.Acquired));
        Assert.Equal(threads - 1, outcomes.Count(o => o == LockOutcome.Busy));
        Assert.DoesNotContain(LockOutcome.Unavailable, outcomes);
        Assert.Equal(1, os.Calls);
        foreach (var holder in holders)
        {
            holder?.Dispose();
        }
        Assert.False(gate.Held);
    }

    [Fact]
    public void Null_acquire_is_rejected()
    {
        var gate = new InProcessLockGate();
        Assert.Throws<ArgumentNullException>(() => gate.TryAcquire(null!, out _, out _));
    }
}
