namespace Nexus.Core.Storage;

/// <summary>
/// The in-process half of the data folder lock. On Windows a process that already holds the protected lock
/// cannot open its private namespace a second time (error 52), so without this gate a second attempt from
/// the same process would be reported as "unavailable" instead of "busy". The gate answers that case itself:
/// while a holder from this process exists, every other attempt is Busy and nothing is opened. Attempts are
/// serialized, so of two concurrent threads one acquires and the other sees Busy. Attempts that reach the
/// real lock keep its outcome and failure text unchanged (Unavailable stays Unavailable: fail closed).
/// </summary>
public sealed class InProcessLockGate
{
    public delegate LockOutcome Acquire(out IDisposable? held, out string? failure);

    private readonly Lock _sync = new();
    private bool _held;

    /// <summary>True while a holder obtained through this gate has not been disposed.</summary>
    public bool Held
    {
        get
        {
            lock (_sync)
            {
                return _held;
            }
        }
    }

    public LockOutcome TryAcquire(Acquire acquire, out IDisposable? held, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        held = null;
        failure = null;
        lock (_sync)
        {
            if (_held)
            {
                return LockOutcome.Busy;
            }
            var outcome = acquire(out var inner, out failure);
            if (outcome != LockOutcome.Acquired || inner is null)
            {
                held = inner;
                return outcome;
            }
            _held = true;
            held = new Release(this, inner);
            return LockOutcome.Acquired;
        }
    }

    private sealed class Release(InProcessLockGate gate, IDisposable inner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                inner.Dispose();
            }
            finally
            {
                // Cleared after the real lock is let go, so the next attempt from this process can open it.
                // If letting go failed, the next attempt reaches the real lock and fails closed there.
                lock (gate._sync)
                {
                    gate._held = false;
                }
            }
        }
    }
}
