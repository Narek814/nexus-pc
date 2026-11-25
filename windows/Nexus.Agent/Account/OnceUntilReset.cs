namespace Nexus.Agent.Account;

/// <summary>
/// Runs an action once per service process until <see cref="Reset"/> (W-ACC): the "account.accessDenied" audit is
/// written once, not on every load, and again after the access was restored and lost a second time.
/// </summary>
internal sealed class OnceUntilReset(Action action)
{
    private int _done;

    public void Run()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0)
        {
            action();
        }
    }

    public void Reset() => Interlocked.Exchange(ref _done, 0);
}
