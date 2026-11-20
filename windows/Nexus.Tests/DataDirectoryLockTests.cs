using Nexus.Core.Storage;
using Nexus.Setup;

namespace Nexus.Tests;

/// <summary>One lock shared by every holder in this test, like the protected one is on a PC.</summary>
internal sealed class FakeLockProvider : IDataDirectoryLockProvider
{
    private readonly object _gate = new();

    public bool Held { get; private set; }
    public LockOutcome? Forced { get; set; }
    public string? ForcedFailure { get; set; }

    public LockOutcome TryAcquire(out IDisposable? held, out string? failure)
    {
        held = null;
        failure = ForcedFailure;
        if (Forced is { } forced)
        {
            return forced;
        }
        lock (_gate)
        {
            if (Held)
            {
                return LockOutcome.Busy;
            }
            Held = true;
        }
        held = new Release(this);
        return LockOutcome.Acquired;
    }

    private sealed class Release(FakeLockProvider owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner.Held = false;
            }
        }
    }
}

/// <summary>SA-T4-R6-N1/N2: datadir commands, installation and removal share one lock; refusals change nothing.</summary>
public sealed class DataDirectoryLockTests
{
    [Fact]
    public void Holder_runs_and_releases_the_lock()
    {
        var locks = new FakeLockProvider();
        var said = new List<string>();
        var ran = 0;
        Assert.Equal(42, ExclusiveRun.Run(locks, said.Add, 1, () =>
        {
            ran++;
            Assert.True(locks.Held);
            return 42;
        }));
        Assert.Equal(1, ran);
        Assert.False(locks.Held);
        Assert.Empty(said);
    }

    [Theory]
    [InlineData("datadir", "install")]
    [InlineData("install", "datadir")]
    [InlineData("uninstall", "datadir repair")]
    [InlineData("datadir finalize", "uninstall --purge")]
    public void While_one_runs_every_other_is_refused_without_running(string first, string second)
    {
        var locks = new FakeLockProvider(); // the same lock for agent datadir commands and the installer
        var said = new List<string>();
        var secondRan = false;
        var code = ExclusiveRun.Run(locks, said.Add, 1, () =>
            ExclusiveRun.Run(locks, said.Add, SetupPlan.ExitLockedByAnotherRun, () =>
            {
                secondRan = true;
                return 0;
            }));
        Assert.False(secondRan, $"{second} ran while {first} held the lock");
        Assert.Equal(SetupPlan.ExitLockedByAnotherRun, code);
        Assert.Equal([ExclusiveRun.BusyText], said);
        Assert.False(locks.Held);
    }

    [Fact]
    public void Busy_and_unavailable_are_told_apart_and_both_fail_closed()
    {
        var said = new List<string>();
        var ran = false;
        var busy = new FakeLockProvider { Forced = LockOutcome.Busy };
        Assert.Equal(1, ExclusiveRun.Run(busy, said.Add, 1, () => { ran = true; return 0; }));
        var unavailable = new FakeLockProvider { Forced = LockOutcome.Unavailable, ForcedFailure = "cannot open the private namespace: Access is denied. (error 5)" };
        Assert.Equal(1, ExclusiveRun.Run(unavailable, said.Add, 1, () => { ran = true; return 0; }));
        Assert.False(ran);
        Assert.Equal(ExclusiveRun.BusyText, said[0]);
        Assert.Equal(ExclusiveRun.UnavailableText + " (cannot open the private namespace: Access is denied. (error 5))", said[1]);
        Assert.NotEqual(said[0], said[1]);
    }

    private static DataAce Allow(string sid) => new(sid, true, 0x1F0001, 0);

    [Fact]
    public void Lock_object_made_by_the_lock_itself_is_accepted()
    {
        Assert.Null(ProtectedLockPolicy.Refusal(DataDirectoryPolicy.Administrators, true, true, [Allow(DataDirectoryPolicy.LocalSystem), Allow(DataDirectoryPolicy.Administrators)]));
        Assert.Null(ProtectedLockPolicy.Refusal(DataDirectoryPolicy.LocalSystem, true, true, [Allow(DataDirectoryPolicy.Administrators)]));
    }

    [Theory]
    [InlineData("owner-user")]
    [InlineData("owner-unknown")]
    [InlineData("owner-trustedinstaller")]
    [InlineData("null-dacl")]
    [InlineData("unprotected")]
    [InlineData("empty")]
    [InlineData("extra-user")]
    [InlineData("everyone")]
    [InlineData("deny")]
    [InlineData("deny-everyone")]
    public void Any_other_owner_or_access_list_makes_the_lock_unavailable(string kind)
    {
        string? owner = DataDirectoryPolicy.Administrators;
        bool present = true, isProtected = true;
        List<DataAce> dacl = [Allow(DataDirectoryPolicy.LocalSystem), Allow(DataDirectoryPolicy.Administrators)];
        switch (kind)
        {
            case "owner-user": owner = FakeDataFileSystem.SomeUser; break;
            case "owner-unknown": owner = null; break;
            case "owner-trustedinstaller": owner = DataDirectoryPolicy.TrustedInstaller; break; // not among the lock's two
            case "null-dacl": present = false; dacl = []; break;
            case "unprotected": isProtected = false; break;
            case "empty": dacl = []; break;
            case "extra-user": dacl.Add(Allow(FakeDataFileSystem.SomeUser)); break;
            case "everyone": dacl.Add(Allow("S-1-1-0")); break;
            case "deny": dacl.Insert(0, new DataAce(DataDirectoryPolicy.Administrators, false, 0x1F0001, 0)); break;
            default: dacl.Insert(0, new DataAce("S-1-1-0", false, 0x100000, 0)); break; // a deny that would stop SYNCHRONIZE
        }
        var refusal = ProtectedLockPolicy.Refusal(owner, present, isProtected, dacl);
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Installer_words_for_the_lock_and_its_exit_code()
    {
        Assert.Equal(7, SetupPlan.ExitLockedByAnotherRun);
        Assert.NotEqual(SetupPlan.ExitDataDirectoryUntrusted, SetupPlan.ExitLockedByAnotherRun);
        Assert.Equal("Сейчас уже выполняется установка, удаление или команда восстановления папки данных Nexus PC. Ничего не изменено; дождитесь её окончания и запустите установщик ещё раз.",
            SetupPlan.LockRefusalText(ExclusiveRun.BusyText));
        Assert.Equal("Не удалось создать или открыть защищённую блокировку Nexus PC, поэтому установщик не может исключить одновременную работу с папкой данных. Ничего не изменено. Подробности: (error 5)",
            SetupPlan.LockRefusalText(ExclusiveRun.UnavailableText + " (error 5)"));
    }
}
