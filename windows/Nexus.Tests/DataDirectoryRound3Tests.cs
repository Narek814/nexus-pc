using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Core.Storage;
using Nexus.Setup;
using static Nexus.Tests.TestPaths;

namespace Nexus.Tests;

/// <summary>Service start type and preconditions as the repair command sees them (test double).</summary>
internal sealed class FakeRepairEnvironment : IRepairEnvironment
{
    private int _calls;

    public string? StartType { get; set; } = "auto";
    public List<string> StartTypeChanges { get; } = [];
    /// <summary>Failures returned by the n-th re-check (1 = right after the confirmation).</summary>
    public Func<int, IReadOnlyList<string>>? FailuresAt { get; set; }
    public bool FailRestore { get; set; }
    public bool FailDisable { get; set; }

    public IReadOnlyList<string> PreconditionFailures() => FailuresAt?.Invoke(++_calls) ?? [];

    public string? ServiceStartType() => StartType;

    public void SetServiceStartType(string startType)
    {
        if (FailDisable && startType == DataDirectoryRepair.DisabledStartType)
        {
            throw new IOException("The service is marked for deletion.");
        }
        if (FailRestore && startType != DataDirectoryRepair.DisabledStartType)
        {
            throw new IOException("Access is denied.");
        }
        StartTypeChanges.Add(startType);
        StartType = startType;
    }
}

/// <summary>Third round of T4: service start type, re-checked preconditions, vanished objects, single read, sanitising.</summary>
public sealed class DataDirectoryRound3Tests
{
    private static readonly string Root = TestPaths.Root;
    private static readonly string Plugins = Root + Rel("/OpenRGB/plugins");
    private readonly FakeDataFileSystem _fs = new();
    private readonly FixedTimeProvider _time = new(Shared.Now);
    private readonly FakeRepairEnvironment _env = new();

    public DataDirectoryRound3Tests()
    {
        _fs.AddDirectory(Pd);
        _fs.AddDirectory(Root, FakeDataFileSystem.SomeUser);
        _fs.AddFile(Root + Sep + "config.json", """{"pcId":"x"}""");
        _fs.AddFile(Root + Sep + "devices.json", """[{"Name":"Phone","PublicKey":"AQID","Permissions":["power"],"PairedAt":"2026-01-01T00:00:00Z"}]""");
        _fs.AddFile(Root + Sep + "audit.log", "line\n", owner: FakeDataFileSystem.SomeUser);
        _fs.AddDirectory(Root + Sep + "OpenRGB");
        _fs.AddDirectory(Plugins);
        _fs.AddFile(Plugins + Sep + "evil.dll", "MZ");
    }

    private DataDirectoryRepair Repair(params string[] answers) =>
        new(_fs, new ScriptedConsole(answers.Length == 0 ? [DataDirectoryRepair.ConfirmationWord] : answers), _time, _env);

    private DataDirectoryRepair Repair(ScriptedConsole console) => new(_fs, console, _time, _env);

    private string Backup => _fs.Nodes.Keys.Single(k => k.StartsWith(Root + DataDirectoryRepair.BackupInfix, StringComparison.Ordinal)
        && !k[(Root.Length + 1)..].Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) && !k.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal));

    private JsonElement Journal => JsonElement.Parse(_fs.ReadAllText(Backup + Sep + DataDirectoryRepair.JournalName)!);

    // ------------------------------------------------------------ service start type (owner decision 2)

    [Fact]
    public void Service_is_disabled_during_the_repair_and_restored_only_after_verification()
    {
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, []));
        Assert.Equal(["disabled", "auto"], _env.StartTypeChanges);
        Assert.Equal("auto", Journal.GetProperty("originalStartType").GetString());
        Assert.True(Journal.GetProperty("serviceRestored").GetBoolean());
        Assert.Equal(RepairJournal.Completed, Journal.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("delayed-auto")]
    [InlineData("demand")]
    public void The_exact_original_start_type_comes_back(string original)
    {
        _env.StartType = original;
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, []));
        Assert.Equal(["disabled", original], _env.StartTypeChanges);
    }

    [Fact]
    public void An_already_disabled_or_missing_service_is_left_alone()
    {
        _env.StartType = "disabled";
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, []));
        Assert.Empty(_env.StartTypeChanges);
    }

    [Fact]
    public void Interrupted_repair_keeps_the_service_disabled_until_continue_finishes()
    {
        _fs.CrashAfterSecurityChanges = 3;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        Assert.Equal("disabled", _env.StartType);
        Assert.True(Journal.GetProperty("serviceDisabled").GetBoolean());
        Assert.NotEmpty(Repair().FindUnfinished(Root)); // what the service start check refuses on

        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).Continue(Root, Backup, []));
        Assert.Equal("auto", _env.StartType);
        Assert.Empty(Repair().FindUnfinished(Root));
    }

    [Fact]
    public void Failure_to_restore_the_start_type_is_not_reported_as_success()
    {
        _env.FailRestore = true;
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Repair(Root, []));
        Assert.Equal("disabled", _env.StartType);
        Assert.NotEqual(RepairJournal.Completed, Journal.GetProperty("state").GetString());
        Assert.NotEmpty(Repair().FindUnfinished(Root));
        Assert.Contains("--continue", console.Text, StringComparison.Ordinal);

        _env.FailRestore = false;
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).Continue(Root, Backup, []));
        Assert.Equal("auto", _env.StartType);
    }

    [Fact]
    public void Restore_acl_keeps_the_service_disabled_and_the_repair_unfinished()
    {
        _fs.CrashAfterSecurityChanges = 2;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).RestoreAcl(Root, Backup, []));
        // Owner decision QA-T4-R3-3, variant A: the start type is NOT put back by restore-acl.
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(["disabled"], _env.StartTypeChanges);
        Assert.Equal(RepairJournal.Restored, Assert.Single(Repair().FindUnfinished(Root)).State);
        Assert.Equal("auto", Journal.GetProperty("originalStartType").GetString()); // still saved for the end
    }

    // ------------------------------------------------------------ preconditions checked again (SA-T4-R2-1)

    [Fact]
    public void Precondition_failing_after_the_confirmation_refuses_before_anything_is_created()
    {
        _env.FailuresAt = n => n == 1 ? ["The Nexus PC service is not stopped (state 4)."] : [];
        var before = _fs.Snapshot(Pd);
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Equal(before, _fs.Snapshot(Pd));
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Empty(_env.StartTypeChanges);
        Assert.Contains("not stopped", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Precondition_failing_between_changes_stops_and_keeps_the_service_disabled()
    {
        _env.FailuresAt = n => n == 4 ? ["Sessions: 2 signed-in Windows sessions."] : [];
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Repair(Root, []));
        Assert.Equal(2, _fs.SecurityChanges); // checks 2 and 3 passed, the 4th stopped before the third change
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(RepairJournal.Stopped, Journal.GetProperty("state").GetString());
        Assert.Contains("preconditions no longer hold", console.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ vanished objects, messages by kind (QA-T4-R2-1)

    [Fact]
    public void Object_that_vanishes_between_listing_and_opening_is_not_a_violation()
    {
        var fs = new FakeDataFileSystem();
        fs.AddDirectory(Pd);
        fs.AddDirectory(Root).Protected = true;
        fs.AddFile(Root + Sep + "audit.log.1", "old");
        fs.AddFile(Root + Sep + "config.json", "{}");
        fs.OnOpen = (path, _) =>
        {
            if (path == Root + Sep + "audit.log.1")
            {
                fs.Nodes.Remove(path); // rotated away by the service
            }
        };
        var scan = DataDirectoryScanner.Scan(fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.True(scan.Trusted, string.Join("\n", scan.Violations)); // no false "refuse" (installer code 6)
        Assert.DoesNotContain(scan.Plan, e => e.RelativePath == "audit.log.1");
    }

    [Fact]
    public void Vanished_data_folder_itself_is_still_reported()
    {
        var fs = new FakeDataFileSystem();
        fs.AddDirectory(Pd);
        var scan = DataDirectoryScanner.Scan(fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.Contains(scan.Violations, v => v.Kind == DataViolationKind.Unreadable);
    }

    [Fact]
    public void Refusal_names_what_was_found()
    {
        _fs.Nodes[Root + Sep + "config.json"].Links = 2;
        var scan = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.Equal([DataViolationKind.HardLinks], DataDirectoryRepair.BlockingKinds(scan));
        Assert.Contains("hard links", DataDirectoryRepair.BlockingSummary(scan), StringComparison.Ordinal);
        Assert.DoesNotContain("junction", DataDirectoryRepair.BlockingSummary(scan), StringComparison.Ordinal);
        Assert.Equal("файлы с жёсткими ссылками", SetupPlan.BlockingKindText(DataViolationKind.HardLinks));
        var console = new ScriptedConsole();
        Repair(console).Check(Root);
        Assert.Contains("files with hard links", console.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ one read for display and hash (SA-T4-R2-2)

    [Fact]
    public void Devices_and_config_are_read_once_for_display_and_hash()
    {
        var opens = 0;
        _fs.OnOpen = (path, access) =>
        {
            if (path == Root + Sep + "devices.json" && access == DataAccess.Read && !_fs.Nodes.Keys.Any(k => k.Contains(".backup-", StringComparison.Ordinal)))
            {
                opens++;
            }
        };
        Repair("no").Repair(Root, []);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void Devices_swapped_after_display_refuses_the_backup()
    {
        var shown = false;
        _fs.OnOpen = (path, access) =>
        {
            if (path == Root + Sep + "devices.json" && access == DataAccess.Read)
            {
                if (shown)
                {
                    _fs.Nodes[path].Content = Encoding.UTF8.GetBytes("""[{"Name":"Evil!","PublicKey":"AQID","Permissions":["power"],"PairedAt":"2026-01-01T00:00:00Z"}]""");
                }
                shown = true;
            }
        };
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Contains("\"Phone\"", console.Text, StringComparison.Ordinal);
        Assert.Contains("changed after they were shown", console.Text, StringComparison.Ordinal);
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Empty(_env.StartTypeChanges);
    }

    // ------------------------------------------------------------ backup copy verified by identity (SA-T4-R2-3)

    [Fact]
    public void Copy_replaced_by_another_file_is_refused()
    {
        _fs.OnOpen = (path, access) =>
        {
            if (access == DataAccess.Read && path.EndsWith(Rel("/data/config.json"), StringComparison.Ordinal))
            {
                _fs.Nodes[path].Id = _fs.NewId();
            }
        };
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Contains("the copy is not the file that was written", console.Text, StringComparison.Ordinal);
        Assert.Equal(0, _fs.SecurityChanges);
    }

    [Fact]
    public void Backup_name_has_a_random_suffix()
    {
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, []));
        Assert.Matches(new Regex(@"\.backup-20261114-100000-[0-9a-f]{8}$"), Backup);
    }

    // ------------------------------------------------------------ sanitising (SA-T4-R2-4)

    [Theory]
    [InlineData("evil\u202Etxt.exe", "evil?txt.exe")]
    [InlineData("a\u0007b\u001Bc", "a?b?c")]
    [InlineData("x\u200Dy\u2066z\u2069", "x?y?z?")]
    [InlineData("line\u2028break", "line?break")]
    [InlineData("Устройства", "Устройства")]
    public void Untrusted_names_are_cleaned_before_printing(string raw, string shown)
    {
        Assert.Equal(shown, DataText.Clean(raw));
        Assert.DoesNotContain('\u202E', new DataViolation(raw, DataViolationKind.UntrustedOwner, raw).ToString());
    }

    [Fact]
    public void Foreign_backup_name_is_cleaned_in_reports()
    {
        var foreign = Root + DataDirectoryRepair.BackupInfix + "x\u202Eexe.";
        _fs.AddDirectory(foreign, FakeDataFileSystem.SomeUser);
        var console = new ScriptedConsole();
        Repair(console).Check(Root);
        Assert.DoesNotContain('\u202E', console.Text);
        Assert.Contains("foreign folder, not used", console.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ protected root everywhere (SA-T4-R2-6)

    [Fact]
    public void Root_that_inherits_access_fails_the_check_and_repair_fixes_it()
    {
        var fs = new FakeDataFileSystem();
        fs.AddDirectory(Pd);
        fs.AddDirectory(Root).Protected = false; // trusted owner and entries, but inherits from the parent
        fs.AddFile(Root + Sep + "config.json", "{}");
        var scan = DataDirectoryScanner.Scan(fs, Root, DataDirectoryPolicy.TrustedSids);
        var violation = Assert.Single(scan.Violations);
        Assert.Equal(DataViolationKind.InheritedAccess, violation.Kind);
        Assert.True(scan.Repairable);
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), _time).Repair(Root, []));
        Assert.True(fs.Nodes[Root].Protected);
        Assert.True(DataDirectoryScanner.Scan(fs, Root, DataDirectoryPolicy.TrustedSids).Trusted);
    }

    // ------------------------------------------------------------ service start refusal text, purge (decisions 1, 3)

    [Fact]
    public void Unfinished_repair_report_tells_how_to_continue_or_undo()
    {
        var report = Nexus.Agent.Storage.AgentPaths.UnfinishedReport(Root, [new UnfinishedRepair(Root + ".backup-x\u202E", "stopped", "")]);
        Assert.Contains("datadir repair --continue", report, StringComparison.Ordinal);
        Assert.Contains("datadir restore-acl", report, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', report);
    }

    [Fact]
    public void Purge_lists_backups_and_never_deletes_them()
    {
        var parent = Path.Combine(Path.GetTempPath(), "nexus-purge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(parent, "NexusPC.backup-20261114-100000-abcdef01"));
            Directory.CreateDirectory(Path.Combine(parent, "NexusPC.backup-20261114-100000-12345678.incomplete"));
            Directory.CreateDirectory(Path.Combine(parent, "Other"));
            var backups = SetupPlan.RemainingBackups(Path.Combine(parent, "NexusPC"));
            Assert.Equal(2, backups.Count);
            Assert.All(backups, b => Assert.True(Directory.Exists(b)));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
