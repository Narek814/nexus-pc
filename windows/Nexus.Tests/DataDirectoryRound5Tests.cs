using System.Text.Json;
using Nexus.Core.Storage;
using static Nexus.Tests.TestPaths;

namespace Nexus.Tests;

/// <summary>
/// Fifth round of T4: tampered real backups block (SA-T4-R4-1), planted folders are not walked and their
/// number is bounded (SA-T4-R4-2), preconditions before the first final change (R4-3), no service to put
/// back (R4-4), one datadir command at a time (R4-5).
/// </summary>
public sealed class DataDirectoryRound5Tests
{
    private static readonly string Root = TestPaths.Root;
    private readonly FakeDataFileSystem _fs = new();
    private readonly FixedTimeProvider _time = new(Shared.Now);
    private readonly FakeRepairEnvironment _env = new();

    public DataDirectoryRound5Tests()
    {
        _fs.AddDirectory(Pd);
        _fs.AddDirectory(Root, FakeDataFileSystem.SomeUser);
        _fs.AddFile(Root + Sep + "config.json", """{"pcId":"x"}""");
        _fs.AddFile(Root + Sep + "devices.json", "[]", aces: [.. FakeDataFileSystem.TrustedAces(), new DataAce(FakeDataFileSystem.Users, true, 0x1F01FF, 0x10)]);
    }

    private DataDirectoryRepair Repair(ScriptedConsole? console = null) =>
        new(_fs, console ?? new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), _time, _env);

    private IReadOnlyList<string> Backups => [.. _fs.Nodes.Keys.Where(k => k.StartsWith(Root + DataDirectoryRepair.BackupInfix, StringComparison.Ordinal)
        && !k[(Root.Length + 1)..].Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) && !k.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private string State(string backup) => JsonElement.Parse(_fs.ReadAllText(backup + Sep + DataDirectoryRepair.JournalName)!).GetProperty("state").GetString()!;

    private string Interrupt()
    {
        _fs.CrashAfterSecurityChanges = 1;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        return Assert.Single(Backups);
    }

    private void FixByHand()
    {
        foreach (var (path, node) in _fs.Nodes.Where(n => n.Key == Root || n.Key.StartsWith(Root + Sep, StringComparison.Ordinal)))
        {
            node.Owner = DataDirectoryPolicy.Administrators;
            node.Aces = FakeDataFileSystem.TrustedAces();
            node.Protected = path == Root;
        }
    }

    // ------------------------------------------------------------ SA-T4-R4-1

    [Theory]
    [InlineData("explorer-continue")] // Explorer adds an ACE for the administrator's own account
    [InlineData("journal-ace")]
    [InlineData("journal-replaced-by-link")]
    [InlineData("unprotected-root")]
    public void Damaging_a_real_unfinished_backup_does_not_release_the_block(string damage)
    {
        var backup = Interrupt();
        switch (damage)
        {
            case "explorer-continue":
                _fs.Nodes[backup].Aces.Add(new DataAce(FakeDataFileSystem.SomeUser, true, 0x1F01FF, 0x3));
                break;
            case "journal-ace":
                _fs.Nodes[backup + Sep + DataDirectoryRepair.JournalName].Aces.Add(new DataAce(FakeDataFileSystem.Users, true, 0x1200A9, 0));
                break;
            case "journal-replaced-by-link":
                _fs.Nodes[backup + Sep + DataDirectoryRepair.JournalName].Kind = DataObjectKind.ReparsePoint;
                break;
            default:
                _fs.Nodes[backup].Protected = false;
                break;
        }
        var blocked = Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Equal(DataDirectoryRepair.Tampered, blocked.State);
        Assert.False(blocked.Foreign);
        var report = Nexus.Agent.Storage.AgentPaths.UnfinishedReport(Root, [blocked]);
        Assert.Contains("rename it", report, StringComparison.Ordinal);

        var changes = _fs.SecurityChanges;
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Continue(Root, backup, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Finalize(Root, backup, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).RestoreAcl(Root, backup, []));
        Assert.Equal(changes, _fs.SecurityChanges);
        Assert.Equal("disabled", _env.StartType);
    }

    [Fact]
    public void Renaming_the_tampered_backup_by_hand_releases_it_and_the_folder_check_decides()
    {
        var backup = Interrupt();
        _fs.Nodes[backup].Aces.Add(new DataAce(FakeDataFileSystem.SomeUser, true, 0x1F01FF, 0x3));
        _fs.MoveDirectory(backup, Abs("/pd/NexusPC-tampered-backup-kept"));
        var (unfinished, violations) = DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.Empty(unfinished);
        Assert.NotEmpty(violations); // the half-repaired folder still has to pass on its own
    }

    // ------------------------------------------------------------ SA-T4-R4-2

    [Fact]
    public void Planted_folder_is_looked_at_once_and_never_walked()
    {
        var planted = Root + DataDirectoryRepair.BackupInfix + "20260101-000000";
        _fs.AddDirectory(planted, FakeDataFileSystem.SomeUser);
        _fs.AddFile(planted + Sep + DataDirectoryRepair.JournalName, "{}", owner: FakeDataFileSystem.SomeUser);
        for (var i = 0; i < 200; i++)
        {
            _fs.AddFile(planted + Sep + "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "x", owner: FakeDataFileSystem.SomeUser);
        }
        var opens = 0;
        _fs.OnOpen = (path, _) =>
        {
            if (path.StartsWith(planted, StringComparison.Ordinal))
            {
                opens++;
            }
        };
        var found = Assert.Single(Repair().FindUnfinished(Root));
        Assert.True(found.Foreign);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void Admin_owned_folder_without_a_journal_is_not_walked_and_does_not_block()
    {
        var stub = Root + DataDirectoryRepair.BackupInfix + "20260101-000000-0badc0de";
        _fs.AddDirectory(stub).Protected = true;
        for (var i = 0; i < 50; i++)
        {
            _fs.AddFile(stub + Sep + "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "x");
        }
        var opens = 0;
        _fs.OnOpen = (path, _) => opens += path.StartsWith(stub, StringComparison.Ordinal) ? 1 : 0;
        Assert.Empty(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Equal(2, opens); // the root and the (missing) journal
    }

    [Fact]
    public void Too_many_admin_owned_folders_named_like_backups_block_after_one_look_at_each_root()
    {
        // Owner decision (QA-T4-R5-1, variant B): only folders owned by Administrators or SYSTEM count.
        for (var i = 0; i <= DataDirectoryRepair.MaxBackupCandidates; i++)
        {
            _fs.AddDirectory(Root + DataDirectoryRepair.BackupInfix + i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)).Protected = true;
        }
        var opens = 0;
        _fs.OnOpen = (path, _) => opens += path.Contains(DataDirectoryRepair.BackupInfix, StringComparison.Ordinal) ? 1 : 0;
        var blocked = Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Equal(DataDirectoryRepair.TooManyBackups, blocked.State);
        Assert.Equal(DataDirectoryRepair.MaxBackupCandidates + 1, opens); // the roots only: no journal, no walk
        Assert.Contains("remove the folders", DataDirectoryRepair.FinishCommand(blocked), StringComparison.Ordinal);
    }

    [Fact]
    public void Any_number_of_folders_owned_by_ordinary_users_blocks_nothing_and_is_never_walked()
    {
        for (var i = 0; i <= DataDirectoryRepair.MaxBackupCandidates + 500; i++)
        {
            var planted = Root + DataDirectoryRepair.BackupInfix + i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            _fs.AddDirectory(planted, FakeDataFileSystem.SomeUser);
            if (i % 1000 == 0)
            {
                _fs.AddFile(planted + Sep + DataDirectoryRepair.JournalName, "{}", owner: FakeDataFileSystem.SomeUser);
            }
        }
        var opens = 0;
        _fs.OnOpen = (path, _) => opens += path.Contains(DataDirectoryRepair.BackupInfix, StringComparison.Ordinal) ? 1 : 0;
        var (unfinished, _) = DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids); // service start, installer and uninstall all use this
        Assert.Empty(unfinished);
        Assert.Equal(DataDirectoryRepair.MaxBackupCandidates + 501, opens); // one look at each root, nothing inside
        Assert.All(Repair().FindUnfinished(Root), r => Assert.True(r.Foreign));
        Assert.DoesNotContain(Repair().FindUnfinished(Root), r => r.State == DataDirectoryRepair.TooManyBackups);
    }

    [Fact]
    public void Incomplete_backups_are_not_counted_or_opened()
    {
        for (var i = 0; i < 20; i++)
        {
            _fs.AddDirectory(Root + DataDirectoryRepair.BackupInfix + i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + DataDirectoryRepair.IncompleteSuffix);
        }
        var opens = 0;
        _fs.OnOpen = (path, _) => opens += path.Contains(DataDirectoryRepair.BackupInfix, StringComparison.Ordinal) ? 1 : 0;
        Assert.Empty(Repair().FindUnfinished(Root));
        Assert.Equal(0, opens);
    }

    // ------------------------------------------------------------ SA-T4-R4-3

    [Fact]
    public void Preconditions_are_checked_before_older_repairs_are_marked_superseded()
    {
        _fs.CrashAfterSecurityChanges = 1;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        var undone = Assert.Single(Backups);
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).RestoreAcl(Root, undone, []));
        _time.Now = _time.Now.AddMinutes(5);
        // New repair: #1 after the confirmation, #2..#4 before each of its 3 changes, #5 in Complete.
        var calls = 0;
        _env.FailuresAt = _ => ++calls == 5 ? ["Sessions: 2 signed-in Windows sessions."] : [];
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair().Repair(Root, []));
        Assert.Equal(5, calls);
        Assert.Equal(RepairJournal.Restored, State(undone)); // not marked superseded
        Assert.NotEqual(RepairJournal.Completed, State(Backups.Single(b => b != undone)));
        Assert.Equal("disabled", _env.StartType);
    }

    // ------------------------------------------------------------ SA-T4-R4-4

    [Fact]
    public void Finalize_completes_without_a_start_type_when_the_service_was_removed()
    {
        var backup = Interrupt();
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).RestoreAcl(Root, backup, []));
        FixByHand();
        _env.StartType = null; // uninstalled meanwhile
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(console).Finalize(Root, backup, []));
        Assert.Equal(RepairJournal.Completed, State(backup));
        Assert.Contains("not installed any more", console.Text, StringComparison.Ordinal);
        Assert.Empty(_env.StartTypeChanges.Skip(1)); // only the "disabled" of the repair itself
    }

    // ------------------------------------------------------------ SA-T4-R4-5

    [Fact]
    public void Only_one_datadir_command_holds_the_lock()
    {
        var name = @"Global\NexusPC.DataDir.Test." + Guid.NewGuid().ToString("N");
        bool OnOtherThread()
        {
            var taken = false;
            var thread = new Thread(() =>
            {
                using var other = DataDirectoryLock.TryAcquire(name); // a mutex belongs to a thread: try from another one
                taken = other is not null;
            });
            thread.Start();
            thread.Join();
            return taken;
        }
        using (var first = DataDirectoryLock.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.False(OnOtherThread());
        }
        Assert.True(OnOtherThread());
    }

    // ------------------------------------------------------------ QA-T4-R5-4 (owner decision 2)

    [Fact]
    public void Completed_backup_changed_afterwards_is_tampered_and_blocks_whatever_its_journal_says()
    {
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, []));
        var backup = Assert.Single(Backups);
        Assert.Equal(RepairJournal.Completed, State(backup));
        Assert.Empty(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);

        // What Explorer's "Continue" does: an access entry for the administrator's own account on the backup.
        _fs.Nodes[backup].Aces.Add(new DataAce(FakeDataFileSystem.SomeUser, true, 0x1F01FF, 0x3));
        Assert.Equal(RepairJournal.Completed, State(backup)); // the journal still claims "completed"

        var blocked = Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Equal(DataDirectoryRepair.Tampered, blocked.State);
        Assert.False(blocked.Foreign);
        Assert.StartsWith(DataDirectoryRepair.TamperedText, blocked.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign folder", blocked.Detail, StringComparison.Ordinal);

        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Finalize(Root, backup, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Continue(Root, backup, []));
        // The exact refusal for each: this backup, the tampered text, nothing about "foreign".
        Assert.Equal(2, console.Lines.Count(l => l.StartsWith(backup + ": " + DataDirectoryRepair.TamperedText, StringComparison.Ordinal)));
        Assert.DoesNotContain("foreign folder", console.Text, StringComparison.Ordinal);
        Assert.Equal("auto", _env.StartType); // nothing put back or taken away by the refusals
    }

    // ------------------------------------------------------------ QA-T4-R5-2

    [Theory]
    [InlineData(@"C:\Users\All Users\NexusPC")]
    [InlineData(@"C:\PROGRA~3\NexusPC")]
    [InlineData(@"\\?\C:\ProgramData\NexusPC")]
    public void Other_names_of_the_local_data_folder_count_as_local(string alias)
    {
        const string local = @"C:\ProgramData\NexusPC";
        _fs.Aliases[alias] = local;
        _fs.Aliases[local] = local;
        Assert.True(DataDirectoryRepair.IsLocalDataFolder(_fs, alias, local));
        _fs.Aliases[@"E:\ProgramData\NexusPC"] = @"E:\ProgramData\NexusPC";
        Assert.False(DataDirectoryRepair.IsLocalDataFolder(_fs, @"E:\ProgramData\NexusPC", local));
    }

    [Fact]
    public void A_folder_that_cannot_be_resolved_counts_as_local_the_safe_side()
    {
        Assert.True(DataDirectoryRepair.IsLocalDataFolder(new UnresolvableFileSystem(_fs), @"E:\x\NexusPC", @"C:\ProgramData\NexusPC"));
    }

    private sealed class UnresolvableFileSystem(FakeDataFileSystem inner) : IDataFileSystem
    {
        public IDataObject Open(string fullPath, DataAccess access) => inner.Open(fullPath, access);
        public IReadOnlyList<string> ListChildren(IDataObject directory) => inner.ListChildren(directory);
        public string ExpectedRootFinalPath(string root) => throw new DataAccessException("cannot resolve " + root);
        public long AvailableBytes(string directory) => inner.AvailableBytes(directory);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IReadOnlyList<string> Siblings(string path, string suffixPrefix) => inner.Siblings(path, suffixPrefix);
        public void CreateProtectedDirectory(string path) => inner.CreateProtectedDirectory(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public Stream CreateFile(string path) => inner.CreateFile(path);
        public void FlushToDisk(Stream stream) => inner.FlushToDisk(stream);
        public DataObjectId IdOf(Stream created) => inner.IdOf(created);
        public void WriteAllTextDurably(string path, string contents) => inner.WriteAllTextDurably(path, contents);
        public string? ReadAllText(string path) => inner.ReadAllText(path);
        public IReadOnlyList<string> ListTree(string path) => inner.ListTree(path);
        public void MoveDirectory(string source, string destination) => inner.MoveDirectory(source, destination);
    }

    // ------------------------------------------------------------ QA-T4-R5-3

    [Theory]
    [InlineData(RepairJournal.Stopped, "NexusPC-Setup.exe --datadir-repair --continue \"B\" (или вернуть права: NexusPC-Setup.exe --datadir-restore-acl \"B\")")]
    [InlineData(RepairJournal.Restored, "NexusPC-Setup.exe --datadir-repair (новое исправление) или, если права исправлены вручную, NexusPC-Setup.exe --datadir-finalize \"B\"")]
    [InlineData(RepairJournal.RestoreFailed, "NexusPC-Setup.exe --datadir-restore-acl \"B\" (ещё раз), затем NexusPC-Setup.exe --datadir-repair")]
    [InlineData(DataDirectoryRepair.Tampered, "копия изменена, ни одна команда ей не доверяет. Проверьте её вручную и, чтобы снять блокировку, переименуйте (от имени администратора) так, чтобы имя не начиналось с NexusPC.backup-; затем NexusPC-Setup.exe --datadir-check")]
    [InlineData(DataDirectoryRepair.TooManyBackups, "удалите папки NexusPC.backup-…, которых вы не создавали, затем NexusPC-Setup.exe --datadir-check")]
    public void Uninstall_refusal_names_installer_commands_exactly(string state, string expected)
    {
        var text = Nexus.Setup.SetupPlan.InstallerFinishText(new UnfinishedRepair("B", state, ""));
        Assert.Equal(expected, text);
        Assert.DoesNotContain("--no command", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" datadir ", text, StringComparison.Ordinal); // never the agent's form
    }
}
