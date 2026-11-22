using Nexus.Setup;
using Xunit;

namespace Nexus.Tests;

/// <summary>A power cut: the operation and everything after it do not happen.</summary>
internal sealed class CrashException() : IOException("crash");

/// <summary>Folders as path → content tag; moves are atomic renames of a folder and everything under it.</summary>
internal sealed class FakeInstallFs : IInstallFileSystem
{
    public Dictionary<string, string> Dirs { get; } = new(StringComparer.Ordinal);
    public string? Journal { get; private set; }
    public List<string> JournalStates { get; } = [];
    public int Operations { get; private set; }
    public int? CrashAt { get; set; }
    public bool Dead { get; set; }
    public Func<string, string, bool>? FailMove { get; set; }
    public Func<string, bool>? FailJournal { get; set; }
    public Func<string, bool>? FailDelete { get; set; }
    public List<string> Protected { get; } = [];

    /// <summary>Paths that open as links or with an untrusted owner (B-3).</summary>
    public HashSet<string> Unsafe { get; } = new(StringComparer.Ordinal);
    public bool StagingFailsCheck { get; set; }
    public List<string> Checked { get; } = [];

    public void CheckObject(string path)
    {
        if (Dead)
        {
            throw new CrashException();
        }
        Checked.Add(path);
        if (Unsafe.Contains(path))
        {
            throw new UnsafeInstallObjectException("reparse point: " + path);
        }
    }

    public void EnsureStaging(string path)
    {
        if (!DirectoryExists(path))
        {
            Op();
            Dirs[path] = "(created staging)";
        }
        if (StagingFailsCheck)
        {
            throw new UnsafeInstallObjectException("staging owner is not Administrators");
        }
    }

    private void Op()
    {
        if (Dead)
        {
            throw new CrashException();
        }
        Operations++;
        if (CrashAt == Operations)
        {
            Dead = true;
            throw new CrashException();
        }
    }

    public bool DirectoryExists(string path)
    {
        if (Dead)
        {
            throw new CrashException();
        }
        return Dirs.ContainsKey(path) || Dirs.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal));
    }

    public void MoveDirectory(string from, string to)
    {
        Op();
        if (FailMove?.Invoke(from, to) == true)
        {
            throw new IOException("in use: " + from);
        }
        if (!Dirs.ContainsKey(from) || Dirs.ContainsKey(to))
        {
            throw new IOException($"cannot move {from} to {to}");
        }
        foreach (var key in Dirs.Keys.Where(k => k == from || k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
        {
            var value = Dirs[key];
            Dirs.Remove(key);
            Dirs[to + key[from.Length..]] = value;
        }
    }

    public void DeleteDirectory(string path)
    {
        Op();
        if (FailDelete?.Invoke(path) == true)
        {
            throw new IOException("in use: " + path);
        }
        foreach (var key in Dirs.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            Dirs.Remove(key);
        }
    }

    public IReadOnlyList<string> ListDirectories(string root) =>
        [.. Dirs.Keys.Where(k => k.StartsWith(root + "/", StringComparison.Ordinal)).Select(k => root + "/" + k[(root.Length + 1)..].Split('/')[0]).Distinct()];

    public string? ReadJournal(string path) => Dead ? throw new CrashException() : Journal;

    public void WriteJournal(string path, string contents)
    {
        Op();
        var state = InstallJournal.Parse(contents, Path.GetDirectoryName(path)!)!.State;
        if (FailJournal?.Invoke(state) == true)
        {
            throw new IOException("disk full");
        }
        Journal = contents;
        JournalStates.Add(state);
    }

    public void DeleteJournal(string path)
    {
        Op();
        Journal = null;
    }

    public void ProtectInstalled(string path)
    {
        Op();
        Protected.Add(path);
    }
}

internal sealed class FakeServices : IInstallServices
{
    public List<string> Calls { get; } = [];
    public bool FailStop { get; set; }
    public bool FailStart { get; set; }
    public Queue<string?> Health { get; } = new();

    public void StopAll()
    {
        Calls.Add("StopAll");
        if (FailStop)
        {
            throw new InvalidOperationException("service does not stop");
        }
    }

    public void StartBack() => Calls.Add("StartBack");

    public void Register() => Calls.Add("Register");

    public RegistrationSnapshot Before { get; set; } = new(false, false, false);
    public RegistrationSnapshot? UnregisteredWith { get; private set; }

    public RegistrationSnapshot Registration() => Before;

    public void Unregister(RegistrationSnapshot before)
    {
        Calls.Add("Unregister");
        UnregisteredWith = before;
    }

    public void Start()
    {
        Calls.Add("Start");
        if (FailStart)
        {
            throw new InvalidOperationException("does not start");
        }
    }

    public void Stop() => Calls.Add("Stop");

    public string? HealthProblem()
    {
        Calls.Add("Health");
        return Health.Count > 0 ? Health.Dequeue() : null;
    }
}

public sealed class InstallTransactionTests
{
    private const string Root = "/pf/NexusPC";
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly string Staging = Path.Join(Root, ".staging-" + Id);
    private static readonly string[] Parts = ["Agent", "Tray", "OpenRGB"];

    private static FakeInstallFs Upgrade()
    {
        var fs = new FakeInstallFs();
        foreach (var p in Parts)
        {
            fs.Dirs[Path.Join(Root, p)] = "old-" + p;
            fs.Dirs[Path.Join(Staging, p)] = "new-" + p;
        }
        fs.Dirs[Path.Join(Root, "Agent", "Nexus.Agent.exe")] = "old-exe";
        return fs;
    }

    private static FakeInstallFs FirstInstall()
    {
        var fs = new FakeInstallFs();
        foreach (var p in Parts)
        {
            fs.Dirs[Path.Join(Staging, p)] = "new-" + p;
        }
        return fs;
    }

    private static TransactionOutcome Run(FakeInstallFs fs, FakeServices services, Action? configure = null) =>
        new InstallTransaction(fs, services, Root, TextWriter.Null).Run(Staging, Parts, configure ?? services.Register);

    private static void AssertOldInPlace(FakeInstallFs fs)
    {
        foreach (var p in Parts)
        {
            Assert.Equal("old-" + p, fs.Dirs[Path.Join(Root, p)]);
            Assert.False(fs.DirectoryExists(InstallJournal.OldPath(Root, p, Id)));
        }
        Assert.Equal("old-exe", fs.Dirs[Path.Join(Root, "Agent", "Nexus.Agent.exe")]);
    }

    private static void AssertNewInPlace(FakeInstallFs fs)
    {
        foreach (var p in Parts)
        {
            Assert.Equal("new-" + p, fs.Dirs[Path.Join(Root, p)]);
            Assert.False(fs.DirectoryExists(InstallJournal.OldPath(Root, p, Id)));
        }
    }

    [Fact]
    public void Successful_update_replaces_every_component_and_commits()
    {
        var fs = Upgrade();
        var services = new FakeServices();
        var tx = new InstallTransaction(fs, services, Root, TextWriter.Null);
        var outcome = tx.Run(Staging, Parts, services.Register);
        Assert.Equal(TransactionResult.Committed, outcome.Result);
        AssertNewInPlace(fs);
        Assert.Equal(["StopAll", "Register", "Start", "Health"], services.Calls);
        Assert.Equal([InstallJournal.Prepared, InstallJournal.Stopping, InstallJournal.Replacing], fs.JournalStates.Distinct().Take(3));
        Assert.Equal(InstallJournal.Committed, fs.JournalStates[^1]);
        Assert.Equal(Parts.Select(p => Path.Join(Staging, p)), fs.Protected); // DACL set before each component is moved in
        tx.Finish(Staging);
        Assert.Null(fs.Journal);
        Assert.False(fs.DirectoryExists(Staging));
    }

    [Fact]
    public void Every_action_is_journaled_before_it_is_done()
    {
        var fs = Upgrade();
        Run(fs, new FakeServices());
        var steps = fs.JournalStates;
        // prepared, stopping, replacing, then per component (old move, new move), registering, starting, committed
        Assert.Equal(3 + (2 * Parts.Length) + 3, steps.Count);
    }

    [Fact]
    public void U2_failure_starts_the_old_version_again_and_moves_nothing()
    {
        var fs = Upgrade();
        var services = new FakeServices { FailStop = true };
        var outcome = Run(fs, services);
        Assert.Equal((TransactionResult.RolledBack, "U2"), (outcome.Result, outcome.Stage));
        AssertOldInPlace(fs);
        Assert.Contains("StartBack", services.Calls);
        Assert.Null(fs.Journal);
        Assert.True(fs.DirectoryExists(Path.Join(Staging, "Agent"))); // staging: removed by the caller
    }

    [Theory]
    [InlineData("Agent", false)]
    [InlineData("Tray", false)]
    [InlineData("Tray", true)]
    [InlineData("OpenRGB", true)]
    public void U3_failure_on_any_move_puts_the_old_components_back(string component, bool newMove)
    {
        var fs = Upgrade();
        var services = new FakeServices();
        fs.FailMove = (from, to) => newMove
            ? from == Path.Join(Staging, component)
            : from == Path.Join(Root, component) && to.Contains(".old-", StringComparison.Ordinal);
        var outcome = Run(fs, services);
        Assert.Equal((TransactionResult.RolledBack, "U3"), (outcome.Result, outcome.Stage));
        AssertOldInPlace(fs);
        foreach (var p in Parts)
        {
            Assert.Equal("new-" + p, fs.Dirs[Path.Join(Staging, p)]);
        }
        Assert.Equal(["StopAll", "StartBack"], services.Calls);
        Assert.Null(fs.Journal);
    }

    [Fact]
    public void U4_failure_rolls_back_and_reapplies_the_old_registration()
    {
        var fs = Upgrade();
        var services = new FakeServices();
        var outcome = Run(fs, services, () => throw new InvalidOperationException("sc.exe failed"));
        Assert.Equal((TransactionResult.RolledBack, "U4"), (outcome.Result, outcome.Stage));
        AssertOldInPlace(fs);
        Assert.Equal(["StopAll", "Register", "StartBack", "Health"], services.Calls);
        Assert.Null(outcome.OldHealthProblem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void U5_unhealthy_or_not_starting_stops_the_new_service_rolls_back_and_checks_the_old_one(bool failStart)
    {
        var fs = Upgrade();
        var services = new FakeServices { FailStart = failStart };
        if (!failStart)
        {
            services.Health.Enqueue("port 47800 is not listening");
        }
        services.Health.Enqueue(null); // the old version is healthy again
        var outcome = Run(fs, services);
        Assert.Equal((TransactionResult.RolledBack, "U5"), (outcome.Result, outcome.Stage));
        AssertOldInPlace(fs);
        Assert.Equal(failStart
            ? ["StopAll", "Register", "Start", "Stop", "Register", "StartBack", "Health"]
            : ["StopAll", "Register", "Start", "Health", "Stop", "Register", "StartBack", "Health"], services.Calls);
        Assert.Null(outcome.OldHealthProblem);
    }

    [Fact]
    public void U5_reports_when_the_restored_old_version_is_not_healthy_either()
    {
        var fs = Upgrade();
        var services = new FakeServices();
        services.Health.Enqueue("status does not answer");
        services.Health.Enqueue("the service is not running");
        var outcome = Run(fs, services);
        Assert.Equal(TransactionResult.RolledBack, outcome.Result);
        Assert.Equal("the service is not running", outcome.OldHealthProblem);
        AssertOldInPlace(fs);
    }

    [Fact]
    public void U6_commit_record_failure_rolls_back_instead_of_leaving_an_uncommitted_new_version()
    {
        var fs = Upgrade();
        fs.FailJournal = state => state == InstallJournal.Committed;
        var services = new FakeServices();
        var outcome = Run(fs, services);
        Assert.Equal((TransactionResult.RolledBack, "U6"), (outcome.Result, outcome.Stage));
        AssertOldInPlace(fs);
        Assert.Contains("Stop", services.Calls);
    }

    [Fact]
    public void U6_cleanup_failure_does_not_undo_the_installation_and_the_next_run_removes_the_rest()
    {
        var fs = Upgrade();
        var tray = InstallJournal.OldPath(Root, "Tray", Id);
        fs.FailDelete = path => path == tray;
        var tx = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null);
        Assert.Equal(TransactionResult.Committed, tx.Run(Staging, Parts, () => { }).Result);
        tx.Finish(Staging);
        Assert.True(fs.DirectoryExists(tray));
        Assert.NotNull(fs.Journal); // committed journal kept while a leftover exists
        fs.FailDelete = null;
        var services = new FakeServices();
        Assert.Equal(RecoveryResult.Cleaned, new InstallTransaction(fs, services, Root, TextWriter.Null).Recover());
        Assert.False(fs.DirectoryExists(tray));
        Assert.Null(fs.Journal);
        Assert.Empty(services.Calls); // a committed run is not touched otherwise
        Assert.Equal("new-Tray", fs.Dirs[Path.Join(Root, "Tray")]);
    }

    [Fact]
    public void Failed_rollback_deletes_nothing_and_the_next_run_finishes_it()
    {
        var fs = Upgrade();
        var services = new FakeServices();
        services.Health.Enqueue("not listening");
        var blocked = true;
        fs.FailMove = (from, to) => blocked && to == Path.Join(Staging, "Tray"); // the new Tray is locked during rollback
        var outcome = Run(fs, services);
        Assert.Equal(TransactionResult.RecoveryFailed, outcome.Result);
        Assert.NotNull(fs.Journal);
        Assert.True(fs.DirectoryExists(InstallJournal.OldPath(Root, "Tray", Id)));
        Assert.Equal("new-Tray", fs.Dirs[Path.Join(Root, "Tray")]);

        blocked = false; // after a reboot
        var next = new FakeServices();
        Assert.Equal(RecoveryResult.RolledBack, new InstallTransaction(fs, next, Root, TextWriter.Null).Recover());
        AssertOldInPlace(fs);
        Assert.Null(fs.Journal);
        Assert.False(fs.DirectoryExists(Staging));
        Assert.Equal(["StopAll", "Register", "StartBack"], next.Calls);
    }

    [Fact]
    public void First_installation_rollback_removes_the_new_files_and_the_registration_only()
    {
        var fs = FirstInstall();
        fs.Dirs["/pd/NexusPC/config.json"] = "data"; // data lives elsewhere and is never touched
        var services = new FakeServices();
        services.Health.Enqueue("status does not answer");
        var outcome = Run(fs, services);
        Assert.Equal((TransactionResult.RolledBack, "U5"), (outcome.Result, outcome.Stage));
        foreach (var p in Parts)
        {
            Assert.False(fs.Dirs.ContainsKey(Path.Join(Root, p)));
            Assert.Equal("new-" + p, fs.Dirs[Path.Join(Staging, p)]);
        }
        Assert.Equal(["StopAll", "Register", "Start", "Health", "Stop", "Unregister"], services.Calls);
        Assert.Equal("data", fs.Dirs["/pd/NexusPC/config.json"]);
    }

    /// <summary>Interrupting a full run (and its Finish) before each single file operation, then running recovery.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interruption_before_any_step_is_recovered_by_the_next_run(bool first)
    {
        var total = OperationsOfAFullRun(first);
        for (var crash = 1; crash <= total; crash++)
        {
            var fs = first ? FirstInstall() : Upgrade();
            fs.CrashAt = crash;
            var tx = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null);
            var committed = false;
            try
            {
                committed = tx.Run(Staging, Parts, () => { }).Result == TransactionResult.Committed;
                tx.Finish(Staging);
            }
            catch (CrashException)
            {
                // the power went off
            }
            var wasCommitted = fs.JournalStates.Contains(InstallJournal.Committed) || (committed && fs.Journal is null);
            fs.Dead = false;
            fs.CrashAt = null;

            var services = new FakeServices();
            var result = new InstallTransaction(fs, services, Root, TextWriter.Null).Recover();
            Assert.NotEqual(RecoveryResult.Failed, result);
            if (wasCommitted)
            {
                AssertNewInPlace(fs);
            }
            else if (first)
            {
                Assert.All(Parts, p => Assert.False(fs.Dirs.ContainsKey(Path.Join(Root, p)), $"crash {crash}: {p} left"));
            }
            else
            {
                AssertOldInPlace(fs);
            }
            Assert.Null(fs.Journal);
            Assert.False(fs.DirectoryExists(Staging), $"crash {crash}: staging left");
            if (!wasCommitted && fs.JournalStates.Count > 0 && first && result == RecoveryResult.RolledBack)
            {
                Assert.Contains("Unregister", services.Calls);
            }
        }
    }

    private static int OperationsOfAFullRun(bool first)
    {
        var fs = first ? FirstInstall() : Upgrade();
        var tx = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null);
        tx.Run(Staging, Parts, () => { });
        tx.Finish(Staging);
        return fs.Operations;
    }

    [Fact]
    public void Old_folder_without_current_one_is_moved_back()
    {
        var fs = Upgrade();
        fs.MoveDirectory(Path.Join(Root, "Agent"), InstallJournal.OldPath(Root, "Agent", Id));
        fs.WriteJournal(InstallJournal.PathIn(Root), new InstallJournal(Id, Staging, false, InstallJournal.Replacing,
            [.. Parts.Select(p => new JournalComponent(p, true))]).Serialize());
        Assert.Equal(RecoveryResult.RolledBack, new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null).Recover());
        AssertOldInPlace(fs);
    }

    [Fact]
    public void Old_and_new_both_present_without_commit_goes_back_to_the_old_version()
    {
        var fs = Upgrade();
        fs.MoveDirectory(Path.Join(Root, "Agent"), InstallJournal.OldPath(Root, "Agent", Id));
        fs.MoveDirectory(Path.Join(Staging, "Agent"), Path.Join(Root, "Agent"));
        fs.WriteJournal(InstallJournal.PathIn(Root), new InstallJournal(Id, Staging, false, InstallJournal.Starting,
            [.. Parts.Select(p => new JournalComponent(p, true))]).Serialize());
        var services = new FakeServices();
        Assert.Equal(RecoveryResult.RolledBack, new InstallTransaction(fs, services, Root, TextWriter.Null).Recover());
        AssertOldInPlace(fs);
        Assert.Equal(["StopAll", "Register", "StartBack"], services.Calls);
    }

    [Fact]
    public void Without_a_journal_only_our_staging_folders_are_removed()
    {
        var fs = Upgrade();
        var foreignOld = Path.Join(Root, "Agent.old-" + Id);
        var notOurs = Path.Join(Root, ".staging-something");
        fs.Dirs[foreignOld] = "unknown";
        fs.Dirs[notOurs] = "unknown";
        Assert.Equal(RecoveryResult.Nothing, new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null).Recover());
        Assert.False(fs.DirectoryExists(Staging));
        Assert.True(fs.DirectoryExists(foreignOld));
        Assert.True(fs.DirectoryExists(notOurs));
        Assert.Equal("old-Agent", fs.Dirs[Path.Join(Root, "Agent")]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":1,"id":"0123456789abcdef0123456789abcdef","staging":"/elsewhere/.staging-0123456789abcdef0123456789abcdef","firstInstall":false,"state":"replacing","components":[]}""")]
    [InlineData("""{"version":1,"id":"0123456789abcdef0123456789abcdef","staging":"/pf/NexusPC/.staging-0123456789abcdef0123456789abcdef","firstInstall":false,"state":"replacing","components":[{"name":"..","hadOld":true}]}""")]
    [InlineData("""{"version":1,"id":"../x","staging":"/pf/NexusPC/.staging-../x","firstInstall":false,"state":"replacing","components":[]}""")]
    [InlineData("""{"version":1,"id":"0123456789abcdef0123456789abcdef","staging":"/pf/NexusPC/.staging-0123456789abcdef0123456789abcdef","firstInstall":false,"state":"whatever","components":[]}""")]
    public void Unreadable_or_foreign_journal_touches_nothing(string journal)
    {
        var fs = Upgrade();
        typeof(FakeInstallFs).GetProperty(nameof(FakeInstallFs.Journal))!.SetValue(fs, journal);
        var before = new Dictionary<string, string>(fs.Dirs);
        var services = new FakeServices();
        Assert.Equal(RecoveryResult.JournalUnreadable, new InstallTransaction(fs, services, Root, TextWriter.Null).Recover());
        Assert.NotNull(fs.Journal); // never deleted automatically
        Assert.Equal(before, fs.Dirs);
        Assert.Empty(services.Calls);
    }

    [Fact]
    public void Unreadable_journal_and_failed_rollback_are_reported_differently()
    {
        var fs = Upgrade();
        typeof(FakeInstallFs).GetProperty(nameof(FakeInstallFs.Journal))!.SetValue(fs, "{broken");
        Assert.Equal(RecoveryResult.JournalUnreadable, new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null).Recover());
        Assert.Contains(InstallJournal.PathIn(Root), SetupPlan.JournalUnreadableText(InstallJournal.PathIn(Root)), StringComparison.Ordinal);
        Assert.NotEqual(SetupPlan.RecoveryFailedText, SetupPlan.JournalUnreadableText(InstallJournal.PathIn(Root)));

        var locked = Upgrade();
        locked.MoveDirectory(Path.Join(Root, "Agent"), InstallJournal.OldPath(Root, "Agent", Id));
        locked.WriteJournal(InstallJournal.PathIn(Root), new InstallJournal(Id, Staging, false, InstallJournal.Replacing,
            [.. Parts.Select(p => new JournalComponent(p, true))]).Serialize());
        locked.FailMove = (_, _) => true;
        Assert.Equal(RecoveryResult.Failed, new InstallTransaction(locked, new FakeServices(), Root, TextWriter.Null).Recover());
    }

    [Theory]
    [InlineData("U3")]
    [InlineData("U4")]
    public void Staging_removed_after_a_failure_is_recreated_and_checked_by_the_next_run(string stage)
    {
        var fs = Upgrade();
        var tx = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null);
        fs.CrashAt = null;
        if (stage == "U3")
        {
            fs.FailMove = (from, _) => from == Path.Join(Staging, "Tray"); // Agent already replaced, Tray fails...
        }
        // ...and the rollback cannot run (power cut right after the failure), so the journal stays.
        var failing = new FakeServices();
        var crashAfterFailure = stage == "U3";
        Action configure = stage == "U4" ? () => { fs.Dead = true; throw new InvalidOperationException("sc"); } : () => { };
        if (crashAfterFailure)
        {
            fs.FailMove = (from, _) =>
            {
                if (from == Path.Join(Staging, "Tray"))
                {
                    fs.Dead = true;
                    return true;
                }
                return false;
            };
        }
        try
        {
            new InstallTransaction(fs, failing, Root, TextWriter.Null).Run(Staging, Parts, configure);
        }
        catch (CrashException)
        {
        }
        fs.Dead = false;
        fs.FailMove = null;
        Assert.NotNull(fs.Journal);
        // Someone (cleanup tool, the person) deleted the staging folder in between.
        fs.DeleteDirectory(Staging);
        Assert.False(fs.DirectoryExists(Staging));

        var result = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null).Recover();
        Assert.Equal(RecoveryResult.RolledBack, result);
        AssertOldInPlace(fs);
        Assert.Null(fs.Journal);
        Assert.False(fs.DirectoryExists(Staging));
    }

    [Fact]
    public void Recreated_staging_that_fails_its_check_stops_the_recovery_without_deleting_anything()
    {
        var fs = Upgrade();
        fs.MoveDirectory(Path.Join(Root, "Agent"), InstallJournal.OldPath(Root, "Agent", Id));
        fs.MoveDirectory(Path.Join(Staging, "Agent"), Path.Join(Root, "Agent"));
        fs.WriteJournal(InstallJournal.PathIn(Root), new InstallJournal(Id, Staging, false, InstallJournal.Replacing,
            [.. Parts.Select(p => new JournalComponent(p, true))]).Serialize());
        fs.StagingFailsCheck = true;
        Assert.Equal(RecoveryResult.Failed, new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null).Recover());
        Assert.True(fs.DirectoryExists(InstallJournal.OldPath(Root, "Agent", Id)));
        Assert.Equal("new-Agent", fs.Dirs[Path.Join(Root, "Agent")]);
        Assert.NotNull(fs.Journal);
    }

    [Fact]
    public void Link_or_foreign_owner_on_an_object_to_move_stops_without_touching_it()
    {
        var fs = Upgrade();
        var old = InstallJournal.OldPath(Root, "Agent", Id);
        fs.MoveDirectory(Path.Join(Root, "Agent"), old);
        fs.WriteJournal(InstallJournal.PathIn(Root), new InstallJournal(Id, Staging, false, InstallJournal.Replacing,
            [.. Parts.Select(p => new JournalComponent(p, true))]).Serialize());
        fs.Unsafe.Add(old);
        Assert.Equal(RecoveryResult.Failed, new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null).Recover());
        Assert.Equal("old-Agent", fs.Dirs[old]);
        Assert.Equal("old-Tray", fs.Dirs[Path.Join(Root, "Tray")]); // checked before the first move: nothing moved
        Assert.NotNull(fs.Journal);
    }

    [Fact]
    public void Link_in_place_of_a_component_refuses_the_update_before_anything_changes()
    {
        var fs = Upgrade();
        fs.Unsafe.Add(Path.Join(Root, "Tray"));
        var services = new FakeServices();
        Assert.Throws<UnsafeInstallObjectException>(() => Run(fs, services));
        AssertOldInPlace(fs);
        Assert.Empty(services.Calls);
        Assert.Null(fs.Journal);
    }

    [Fact]
    public void Committed_cleanup_never_deletes_a_link()
    {
        var fs = Upgrade();
        var tx = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null);
        Assert.Equal(TransactionResult.Committed, tx.Run(Staging, Parts, () => { }).Result);
        // (DeleteOlds already ran; put a "link" back where an old folder was)
        var old = InstallJournal.OldPath(Root, "Tray", Id);
        fs.Dirs[old] = "link target";
        fs.Unsafe.Add(old);
        var left = tx.Finish(Staging);
        Assert.Equal("link target", fs.Dirs[old]);
        Assert.Contains(old, left);
        Assert.Contains(InstallJournal.PathIn(Root), left);
        Assert.NotNull(fs.Journal);
    }

    [Fact]
    public void Every_move_and_delete_checks_its_object_first()
    {
        var fs = Upgrade();
        var tx = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null);
        tx.Run(Staging, Parts, () => { });
        tx.Finish(Staging);
        foreach (var p in Parts)
        {
            Assert.Contains(Path.Join(Root, p), fs.Checked);
            Assert.Contains(Path.Join(Staging, p), fs.Checked);
            Assert.Contains(InstallJournal.OldPath(Root, p, Id), fs.Checked);
        }
    }

    [Fact]
    public void Finish_reports_nothing_left_when_everything_was_removed()
    {
        var fs = Upgrade();
        var tx = new InstallTransaction(fs, new FakeServices(), Root, TextWriter.Null);
        tx.Run(Staging, Parts, () => { });
        Assert.Empty(tx.Finish(Staging));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void First_installation_rollback_removes_only_registrations_it_added(bool service, bool firewall, bool shortcut)
    {
        var fs = FirstInstall();
        var services = new FakeServices { Before = new RegistrationSnapshot(service, firewall, shortcut) };
        services.Health.Enqueue("no");
        Run(fs, services);
        Assert.Equal(new RegistrationSnapshot(service, firewall, shortcut), services.UnregisteredWith);
    }

    [Fact]
    public void Journal_without_registration_fields_counts_everything_as_existing()
    {
        const string old = """{"version":1,"id":"0123456789abcdef0123456789abcdef","staging":"/pf/NexusPC/.staging-0123456789abcdef0123456789abcdef","firstInstall":true,"state":"starting","step":"","components":[{"name":"Agent","hadOld":false}]}""";
        Assert.Equal(RegistrationSnapshot.AllExisted, InstallJournal.Parse(old, Root)!.Before);
        var withFields = new InstallJournal(Id, Staging, true, InstallJournal.Starting, [new("Agent", false)]) { Before = new(false, true, false) };
        Assert.Equal(new RegistrationSnapshot(false, true, false), InstallJournal.Parse(withFields.Serialize(), Root)!.Before);
    }

    [Fact]
    public void Journal_round_trips()
    {
        var journal = new InstallJournal(Id, Staging, true, InstallJournal.Registering, [new("Agent", false), new("Tray", true)], "step");
        Assert.Equal(journal.Serialize(), InstallJournal.Parse(journal.Serialize(), Root)!.Serialize());
    }
}
