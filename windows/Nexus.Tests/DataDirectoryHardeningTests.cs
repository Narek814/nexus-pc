using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Storage;
using Nexus.Setup;

namespace Nexus.Tests;

/// <summary>Second round of T4: untrusted backups, journal validation, interruptions, presence, sessions, limits.</summary>
public sealed class DataDirectoryHardeningTests
{
    private const string Root = "/pd/NexusPC";
    private static readonly string Plugins = Root + "/OpenRGB/plugins";
    private readonly FakeDataFileSystem _fs = new();
    private readonly FixedTimeProvider _time = new(Shared.Now);

    public DataDirectoryHardeningTests()
    {
        _fs.AddDirectory("/pd");
        _fs.AddDirectory(Root, FakeDataFileSystem.SomeUser);
        _fs.AddFile(Root + "/config.json", """{"pcId":"x"}""");
        _fs.AddFile(Root + "/devices.json", """[{"Name":"Phone","PublicKey":"AQID","Permissions":["power"],"PairedAt":"2026-01-01T00:00:00Z"}]""",
            aces: [.. FakeDataFileSystem.TrustedAces(), new DataAce(FakeDataFileSystem.Users, true, 0x1F01FF, 0x10)]);
        _fs.AddFile(Root + "/audit.log", "line\n", owner: FakeDataFileSystem.SomeUser);
        _fs.AddDirectory(Root + "/OpenRGB");
        _fs.AddFile(Root + "/OpenRGB/OpenRGB.json", "{}");
        _fs.AddDirectory(Plugins);
        _fs.AddFile(Plugins + "/evil.dll", "MZ");
    }

    private DataDirectoryRepair Repair(ScriptedConsole? console = null) => new(_fs, console ?? new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), _time);

    private string Backup => _fs.Nodes.Keys.Single(k => k.StartsWith(Root + DataDirectoryRepair.BackupInfix, StringComparison.Ordinal)
        && !k[(Root.Length + 1)..].Contains('/', StringComparison.Ordinal) && !k.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal) && !k.Contains("2025", StringComparison.Ordinal));

    private static string Journal(string backup) => backup + "/" + DataDirectoryRepair.JournalName;

    private void Interrupt()
    {
        _fs.CrashAfterSecurityChanges = 2;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
    }

    private void EditJournal(Action<JsonObject> change)
    {
        var path = Journal(Backup);
        var json = JsonNode.Parse(_fs.ReadAllText(path)!)!.AsObject();
        change(json);
        _fs.Nodes[path].Content = Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private string ForeignBackup(Action<FakeDataFileSystem.Node> spoil)
    {
        var backup = Root + DataDirectoryRepair.BackupInfix + "20250101-000000";
        var node = _fs.AddDirectory(backup);
        node.Protected = true;
        _fs.AddFile(backup + "/" + DataDirectoryRepair.JournalName, $$"""{"format":1,"root":"{{Root}}","rootFinalPath":"{{Root}}","backup":"{{backup}}","startedAt":"2025-01-01T00:00:00Z","manifestSha256":"x","originalSecuritySha256":"y","state":"acl-in-progress","steps":[],"problems":[]}""");
        spoil(node);
        return backup;
    }

    // ------------------------------------------------------------ untrusted backups (SA-T4-1)

    [Theory]
    [InlineData("owner")]
    [InlineData("ace")]
    [InlineData("unprotected")]
    [InlineData("reparse-journal")]
    public void Planted_backup_is_foreign_and_ignored_but_an_admin_owned_changed_one_blocks(string spoil)
    {
        var foreign = ForeignBackup(node =>
        {
            switch (spoil)
            {
                case "owner": node.Owner = FakeDataFileSystem.SomeUser; break;
                case "ace": node.Aces.Add(new DataAce(FakeDataFileSystem.Users, true, 0x1F01FF, 0x3)); break;
                case "unprotected": node.Protected = false; break;
                default: _fs.Nodes[Root + DataDirectoryRepair.BackupInfix + "20250101-000000/" + DataDirectoryRepair.JournalName].Kind = DataObjectKind.ReparsePoint; break;
            }
        });
        var found = Assert.Single(Repair().FindUnfinished(Root));
        if (spoil != "owner")
        {
            // Owner decision (SA-T4-R4-1): an Administrators-owned backup with a journal that no longer passes
            // the check is tampered: never used, and it BLOCKS (fail closed) instead of being ignored.
            AssertTamperedBlocks(foreign, found);
            return;
        }
        Assert.True(found.Foreign);

        var check = new ScriptedConsole();
        Repair(check).Check(Root);
        Assert.Contains("foreign folder, not used", check.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue: datadir repair --continue \"" + foreign, check.Text, StringComparison.Ordinal);

        var before = _fs.Snapshot("/pd");
        var refused = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(refused).RestoreAcl(Root, foreign, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(refused).Continue(Root, foreign, []));
        Assert.Equal(2, refused.Lines.Count(l => l.Contains("foreign folder, not used", StringComparison.Ordinal))); // refused for distrust, before the journal is read
        Assert.Equal(before, _fs.Snapshot("/pd"));
        Assert.Equal(0, _fs.SecurityChanges);

        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, [])); // not blocked by it
    }

    private void AssertTamperedBlocks(string tampered, UnfinishedRepair found)
    {
        Assert.False(found.Foreign);
        Assert.Equal(DataDirectoryRepair.Tampered, found.State);
        Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished); // the service does not start
        var before = _fs.Snapshot("/pd");
        var refused = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(refused).RestoreAcl(Root, tampered, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(refused).Continue(Root, tampered, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(refused).Finalize(Root, tampered, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(refused).Repair(Root, [])); // a new repair waits too
        Assert.Equal(before, _fs.Snapshot("/pd"));
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Contains("tampered", refused.Text, StringComparison.Ordinal);
        Assert.Contains("rename it", refused.Text, StringComparison.Ordinal); // the way out is named
    }

    [Theory]
    [InlineData("rename-up")]
    [InlineData("rename-absolute")]
    [InlineData("path-absolute")]
    [InlineData("path-up")]
    [InlineData("action")]
    [InlineData("root")]
    [InlineData("root-final")]
    [InlineData("backup")]
    public void Tampered_journal_is_refused_without_changes(string tamper)
    {
        Interrupt();
        EditJournal(json =>
        {
            var steps = json["steps"]!.AsArray();
            switch (tamper)
            {
                case "rename-up": steps[^1]!["renameTo"] = "..\\x"; break;
                case "rename-absolute": steps[^1]!["renameTo"] = "C:\\Windows\\x"; break;
                case "path-absolute": steps[3]!["path"] = "/etc/passwd"; break;
                case "path-up": steps[3]!["path"] = "../other/file"; break;
                case "action": steps[3]!["action"] = "delete"; break;
                case "root": json["root"] = "/pd/Other"; break;
                case "root-final": json["rootFinalPath"] = "/elsewhere/NexusPC"; break;
                default: json["backup"] = "/pd/NexusPC.backup-other"; break;
            }
        });
        var changes = _fs.SecurityChanges;
        var before = _fs.Snapshot(Root);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Continue(Root, Backup, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).RestoreAcl(Root, Backup, []));
        Assert.Equal(changes, _fs.SecurityChanges);
        Assert.Equal(before, _fs.Snapshot(Root));
    }

    [Fact]
    public void Backup_of_another_folder_is_refused()
    {
        Interrupt();
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).RestoreAcl("/pd/Other/NexusPC", Backup, []));
        Assert.Contains("not a backup of", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_original_security_file_refuses_continue_and_restore()
    {
        Interrupt();
        var path = Backup + "/" + DataDirectoryRepair.OriginalSecurityName;
        _fs.Nodes[path].Content = [.. _fs.Nodes[path].Content, (byte)' '];
        var changes = _fs.SecurityChanges;
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Continue(Root, Backup, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).RestoreAcl(Root, Backup, []));
        Assert.Equal(changes, _fs.SecurityChanges);
    }

    [Fact]
    public void Continue_after_restore_acl_is_refused()
    {
        Interrupt();
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).RestoreAcl(Root, Backup, []));
        var changes = _fs.SecurityChanges;
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Continue(Root, Backup, []));
        Assert.Equal(changes, _fs.SecurityChanges);
    }

    // ------------------------------------------------------------ interruptions and verification

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)] // after the rename
    public void Crash_after_a_change_but_before_the_journal_can_be_continued(int k)
    {
        if (k < 0)
        {
            _fs.CrashAfterRenameBeforeJournal = true;
        }
        else
        {
            _fs.CrashAfterChangeBeforeJournal = k;
        }
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterChangeBeforeJournal = null;
        _fs.CrashAfterRenameBeforeJournal = false;

        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(console).Continue(Root, Backup, []));
        Assert.True(DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids).Trusted, console.Text);
        Assert.Equal(DataDirectoryPolicy.Administrators, _fs.Nodes[Root].Owner);
        Assert.True(_fs.Nodes.ContainsKey(Root + "/OpenRGB/plugins.disabled-20261114-100000/evil.dll"));
    }

    [Fact]
    public void Content_changed_after_the_changes_fails_verification_with_code_3()
    {
        var done = false;
        _fs.OnOpen = (path, access) =>
        {
            if (!done && access == DataAccess.Read && _fs.Renames == 1)
            {
                done = true;
                _fs.Nodes[Root + "/config.json"].Content = Encoding.UTF8.GetBytes("""{"pcId":"y"}""");
            }
        };
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Repair(Root, []));
        var journal = JsonDocument.Parse(_fs.ReadAllText(Journal(Backup))!);
        Assert.Equal(RepairJournal.VerifyFailed, journal.RootElement.GetProperty("state").GetString());
        Assert.Contains("config.json: content differs", console.Text, StringComparison.Ordinal);
        Assert.Contains(Backup, console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void File_changed_between_display_and_copy_with_the_same_size_is_refused()
    {
        _fs.OnOpen = (path, access) =>
        {
            if (path == Root + "/devices.json" && access == DataAccess.Read && _fs.Nodes.Keys.Any(k => k.Contains(".backup-", StringComparison.Ordinal)))
            {
                var bytes = _fs.Nodes[path].Content;
                bytes[^2] = (byte)'X'; // same size, different content
            }
        };
        var before = _fs.Snapshot(Root + "/OpenRGB");
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Contains("changed after they were shown", console.Text, StringComparison.Ordinal);
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Equal(before, _fs.Snapshot(Root + "/OpenRGB"));
        Assert.Contains(_fs.Nodes.Keys, k => k.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void Journal_write_failing_before_changes_is_code_1_and_after_is_code_3()
    {
        _fs.FailDurableWrite = path => path.EndsWith(DataDirectoryRepair.JournalName, StringComparison.Ordinal);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair().Repair(Root, []));
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Contains(_fs.Nodes.Keys, k => k.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal));

        var fresh = new DataDirectoryHardeningTests();
        var writes = 0;
        fresh._fs.FailDurableWrite = path => path.EndsWith(DataDirectoryRepair.JournalName, StringComparison.Ordinal) && ++writes == 3;
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitStopped, fresh.Repair(console).Repair(Root, []));
        Assert.Equal(1, fresh._fs.SecurityChanges);
        Assert.Contains(fresh.Backup, console.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ plugins (owner decision 2)

    [Fact]
    public void Trusted_folder_with_an_empty_plugins_folder_needs_no_repair()
    {
        var fs = new FakeDataFileSystem();
        fs.AddDirectory("/pd");
        fs.AddDirectory(Root).Protected = true;
        fs.AddDirectory(Root + "/OpenRGB");
        fs.AddDirectory(Plugins);
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, console, _time).Repair(Root, []));
        Assert.Contains("Nothing to repair", console.Text, StringComparison.Ordinal);
        Assert.True(fs.Nodes.ContainsKey(Plugins));
        Assert.DoesNotContain(fs.Nodes.Keys, k => k.Contains(".backup-", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_plugins_folder_is_not_renamed_by_a_repair()
    {
        _fs.Nodes.Remove(Plugins + "/evil.dll");
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, []));
        Assert.True(_fs.Nodes.ContainsKey(Plugins));
        Assert.Equal(0, _fs.Renames);
    }

    // ------------------------------------------------------------ parent folder (SA-T4-9)

    [Fact]
    public void Parent_that_others_may_delete_from_refuses_before_anything_is_created()
    {
        _fs.Nodes["/pd"].Aces.Add(new DataAce(FakeDataFileSystem.Users, true, 0x40 /* FILE_DELETE_CHILD */, 0x3));
        var before = _fs.Snapshot("/pd");
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Equal(before, _fs.Snapshot("/pd"));
        Assert.Contains("where the backup would go", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramData_like_parent_is_accepted()
    {
        var programData = new DataObjectState
        {
            FullPath = "C:\\ProgramData",
            Kind = DataObjectKind.Directory,
            OwnerSid = DataDirectoryPolicy.LocalSystem,
            Dacl =
            [
                new(DataDirectoryPolicy.LocalSystem, true, DataDirectoryPolicy.FullControl, 0x3),
                new(DataDirectoryPolicy.Administrators, true, DataDirectoryPolicy.FullControl, 0x3),
                new("S-1-3-0", true, 0x10000000, 0x0B), // CREATOR OWNER, inherit only
                new(FakeDataFileSystem.Users, true, 0x1200A9, 0x3),
                new(FakeDataFileSystem.Users, true, 0x116, 0x2), // write data / add subfolder
            ],
        };
        Assert.Empty(DataDirectoryPolicy.CheckParent(programData, DataDirectoryPolicy.TrustedSids));
        Assert.NotEmpty(DataDirectoryPolicy.CheckParent(programData with { OwnerSid = FakeDataFileSystem.SomeUser }, DataDirectoryPolicy.TrustedSids));
        Assert.NotEmpty(DataDirectoryPolicy.CheckParent(programData with { Dacl = [new(FakeDataFileSystem.Users, true, 0x00010000, 0)] }, DataDirectoryPolicy.TrustedSids));
    }

    [Theory]
    [InlineData("check --data-dir D:\\Data")]
    [InlineData("repair --data-dir /mnt/x/NexusPC.old")]
    [InlineData("restore-acl B --data-dir C:\\")]
    public void Data_dir_must_name_a_NexusPC_folder(string line)
    {
        Assert.Null(DataDirectoryCommandLine.Parse(line.Split(' ')));
    }

    // ------------------------------------------------------------ root, limits, names

    [Fact]
    public void Root_counts_as_repaired_only_with_a_protected_dacl()
    {
        var root = new DataObjectState { FullPath = Root, Kind = DataObjectKind.Directory, OwnerSid = DataDirectoryPolicy.Administrators, Dacl = FakeDataFileSystem.TrustedAces() };
        Assert.False(DataDirectoryPolicy.IsRepaired(root, isRoot: true));
        Assert.True(DataDirectoryPolicy.IsRepaired(root with { DaclProtected = true }, isRoot: true));
        Assert.True(DataDirectoryPolicy.IsRepaired(root, isRoot: false));
    }

    [Fact]
    public void Depth_and_object_limits_are_reported()
    {
        var path = Root;
        for (var i = 0; i < 4; i++)
        {
            path += "/d" + i;
            _fs.AddDirectory(path);
        }
        Assert.Contains(DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids, maxDepth: 2, maxObjects: 1000).Violations,
            v => v.Kind == DataViolationKind.TooLarge && v.Detail.Contains("deeper", StringComparison.Ordinal));
        var limited = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids, maxDepth: 32, maxObjects: 3);
        Assert.Contains(limited.Violations, v => v.Kind == DataViolationKind.TooLarge && v.Detail.Contains("objects", StringComparison.Ordinal));
        Assert.False(limited.Repairable);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a:stream")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Invalid_names_in_a_listing_block_repair(string name)
    {
        _fs.ExtraNamesIn = Root + "/OpenRGB";
        _fs.ExtraNames = [name];
        var scan = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.Contains(scan.Violations, v => v.Detail == "invalid name in folder listing");
        Assert.False(scan.Repairable);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("devices.json", true)]
    [InlineData("OpenRGB/plugins/x.dll", true)]
    [InlineData("OpenRGB\\plugins", true)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:\\Windows", false)]
    [InlineData("a/../b", false)]
    [InlineData("..", false)]
    [InlineData("a//b", false)]
    [InlineData("x:y", false)]
    public void Journal_paths_must_be_relative_and_inside(string path, bool safe)
    {
        Assert.Equal(safe, DataDirectoryPolicy.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("plugins.disabled-20261114-100000", true)]
    [InlineData("plugins.disabled-2026111-100000", false)]
    [InlineData("..\\x", false)]
    [InlineData("plugins.disabled-20261114-100000\\..\\..", false)]
    [InlineData("plugins", false)]
    public void Quarantine_name_has_one_exact_form(string name, bool valid)
    {
        Assert.Equal(valid, DataDirectoryPolicy.IsQuarantineName(name));
    }

    // ------------------------------------------------------------ presence, sessions, trust set

    [Fact]
    public void Any_agent_pipe_or_installed_process_refuses()
    {
        const string install = "C:\\Program Files\\NexusPC";
        Assert.Empty(AgentPresencePolicy.Refusals([], [new RunningProcess(10, "OpenRGB", "D:\\Tools\\OpenRGB\\OpenRGB.exe")], 99, install));
        Assert.Single(AgentPresencePolicy.Refusals([new ExistingPipe("NexusPC.Admin", 1234, "C:\\Program Files\\NexusPC\\Agent\\Nexus.Agent.exe")], [], 99, install));
        Assert.Single(AgentPresencePolicy.Refusals([new ExistingPipe("nexuspc.session")], [], 99, install));
        Assert.Single(AgentPresencePolicy.Refusals([], [new RunningProcess(5, "Nexus.Agent", install + "\\Agent\\Nexus.Agent.exe")], 99, install));
        Assert.Single(AgentPresencePolicy.Refusals([], [new RunningProcess(6, "OpenRGB", install + "\\OpenRGB\\OpenRGB.exe")], 99, install));
        Assert.Single(AgentPresencePolicy.Refusals([], [new RunningProcess(7, "Nexus.Agent", null)], 99, install)); // fail closed
        Assert.Empty(AgentPresencePolicy.Refusals([], [new RunningProcess(99, "Nexus.Agent", install + "\\Agent\\Nexus.Agent.exe")], 99, install)); // this command
    }

    [Fact]
    public void Unreadable_session_user_refuses()
    {
        Assert.NotNull(InteractiveSessionPolicy.Refusal([new(1, "Active", "owner", "Console"), new(2, "Disconnected", null, "", QueryFailed: true)], 1));
    }

    [Fact]
    public void Production_trust_set_is_exactly_the_three_subjects()
    {
        Assert.Same(DataDirectoryPolicy.TrustedSids, Nexus.Agent.Storage.AgentPaths.TrustedSids(null));
        Assert.Equal(3, Nexus.Agent.Storage.AgentPaths.TrustedSids(null).Count);
        var test = new HashSet<string>(["S-1-5-21-9-9-9-1000"]);
        Assert.Same(test, Nexus.Agent.Storage.AgentPaths.TrustedSids(test));
    }

    // ------------------------------------------------------------ installer arguments

    [Theory]
    [InlineData("--datadir-check", "check", null)]
    [InlineData("--datadir-repair --quiet", "repair", null)]
    [InlineData("--datadir-repair --continue X", "continue", "X")]
    [InlineData("--datadir-restore-acl X", "restore-acl", "X")]
    public void Installer_offers_the_same_datadir_commands(string line, string verb, string? backup)
    {
        var options = SetupOptions.Parse(line.Split(' '));
        Assert.NotNull(options?.DataDirectory);
        Assert.Equal(verb, options.DataDirectory.Verb);
        Assert.Equal(backup, options.DataDirectory.Backup);
        Assert.False(options.Uninstall);
    }

    [Theory]
    [InlineData("--datadir-restore-acl")]
    [InlineData("--datadir-check --purge")]
    [InlineData("--datadir-delete")]
    public void Installer_refuses_unknown_datadir_arguments(string line)
    {
        Assert.Null(SetupOptions.Parse(line.Split(' ')));
    }

    [Fact]
    public void Fingerprint_is_lowercase_sha256()
    {
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("a"u8)), DataDirectoryRepair.Fingerprint("a"));
    }
}
