using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Storage;
using Nexus.Setup;

namespace Nexus.Tests;

/// <summary>
/// Fourth round of T4: the start check reports an unfinished repair first (QA-T4-R3-1); restore-acl does not
/// finish a repair and "datadir finalize" does, after every check (QA-T4-R3-3, owner's variant A); messages and
/// codes tell the real state of the service when it cannot be disabled (QA-T4-R3-2).
/// </summary>
public sealed class DataDirectoryRound4Tests
{
    private const string Root = "/pd/NexusPC";
    private static readonly string Plugins = Root + "/OpenRGB/plugins";
    private readonly FakeDataFileSystem _fs = new();
    private readonly FixedTimeProvider _time = new(Shared.Now);
    private readonly FakeRepairEnvironment _env = new();

    public DataDirectoryRound4Tests()
    {
        _fs.AddDirectory("/pd");
        _fs.AddDirectory(Root, FakeDataFileSystem.SomeUser);
        _fs.AddFile(Root + "/config.json", """{"pcId":"x"}""");
        _fs.AddFile(Root + "/devices.json", "[]", aces: [.. FakeDataFileSystem.TrustedAces(), new DataAce(FakeDataFileSystem.Users, true, 0x1F01FF, 0x10)]);
        _fs.AddDirectory(Root + "/OpenRGB");
        _fs.AddDirectory(Plugins);
        _fs.AddFile(Plugins + "/evil.dll", "MZ");
    }

    private DataDirectoryRepair Repair(ScriptedConsole? console = null) =>
        new(_fs, console ?? new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), _time, _env);

    private IReadOnlyList<string> Backups => [.. _fs.Nodes.Keys.Where(k => k.StartsWith(Root + DataDirectoryRepair.BackupInfix, StringComparison.Ordinal)
        && !k[(Root.Length + 1)..].Contains('/', StringComparison.Ordinal) && !k.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private string Backup => Assert.Single(Backups);

    private string State(string backup) => JsonElement.Parse(_fs.ReadAllText(backup + "/" + DataDirectoryRepair.JournalName)!).GetProperty("state").GetString()!;

    /// <summary>A repair interrupted after two changes, then undone by restore-acl.</summary>
    private void InterruptAndUndo()
    {
        _fs.CrashAfterSecurityChanges = 2;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).RestoreAcl(Root, Backup, []));
    }

    private void WithoutPluginContent() => _fs.Nodes.Remove(Plugins + "/evil.dll");

    /// <summary>What an administrator does by hand: trusted owner and entries everywhere, protected root.</summary>
    private void FixByHand()
    {
        foreach (var (path, node) in _fs.Nodes.Where(n => n.Key == Root || n.Key.StartsWith(Root + "/", StringComparison.Ordinal)))
        {
            node.Owner = DataDirectoryPolicy.Administrators;
            node.Aces = FakeDataFileSystem.TrustedAces();
            node.Protected = path == Root;
        }
    }

    // ------------------------------------------------------------ QA-T4-R3-1: unfinished first

    [Fact]
    public void Start_check_reports_an_unfinished_repair_before_the_folder_problems()
    {
        _fs.CrashAfterSecurityChanges = 2;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        Assert.NotEmpty(DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids).Violations); // still has problems

        var (unfinished, violations) = DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.Single(unfinished);
        Assert.Empty(violations); // not reported first: the unfinished repair is what must be dealt with
        var report = Nexus.Agent.Storage.AgentPaths.UnfinishedReport(Root, unfinished);
        Assert.Contains("datadir repair --continue \"" + Backup + "\"", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_check_after_restore_acl_names_the_finishing_commands()
    {
        InterruptAndUndo();
        var (unfinished, _) = DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids);
        var report = Nexus.Agent.Storage.AgentPaths.UnfinishedReport(Root, unfinished);
        Assert.Contains("datadir finalize \"" + Backup + "\"", report, StringComparison.Ordinal);
        Assert.Contains("datadir repair", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_check_without_unfinished_repairs_reports_the_violations()
    {
        var (unfinished, violations) = DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.Empty(unfinished);
        Assert.NotEmpty(violations);
    }

    // ------------------------------------------------------------ QA-T4-R3-3: restore-acl, then finalize

    [Fact]
    public void After_restore_acl_the_start_type_is_not_back_and_the_service_is_blocked()
    {
        InterruptAndUndo();
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(RepairJournal.Restored, State(Backup));
        Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
    }

    [Fact]
    public void Finalize_refuses_while_the_folder_fails_its_checks_and_keeps_everything_blocked()
    {
        InterruptAndUndo();
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Finalize(Root, Backup, []));
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(RepairJournal.Restored, State(Backup));
        Assert.Contains("Not finalized", console.Text, StringComparison.Ordinal);
        Assert.Contains("refuses to start", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_after_a_manual_fix_puts_the_start_type_back_last_and_completes()
    {
        WithoutPluginContent(); // a manual fix may change access only: contents must still match the manifest
        InterruptAndUndo();
        FixByHand();
        var console = new ScriptedConsole();
        var seen = new List<(string Path, string? StartType)>();
        _fs.OnOpen = (path, _) => seen.Add((path, _env.StartType));
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(console).Finalize(Root, Backup, []));
        Assert.Contains(seen, s => s.Path.StartsWith(Root + "/", StringComparison.Ordinal));
        Assert.Contains(seen, s => s.Path.StartsWith(Backup + "/data/", StringComparison.Ordinal));
        Assert.All(seen, s => Assert.Equal("disabled", s.StartType)); // every read before the start type went back
        Assert.Equal("auto", _env.StartType);
        Assert.Equal(RepairJournal.Completed, State(Backup));
        Assert.Empty(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Contains("Repair finalized", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_failing_to_put_back_the_start_type_stays_unfinished_and_blocked()
    {
        WithoutPluginContent(); // a manual fix may change access only: contents must still match the manifest
        InterruptAndUndo();
        FixByHand();
        _env.FailRestore = true;
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Finalize(Root, Backup, []));
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(RepairJournal.Restored, State(Backup)); // not marked completed
        Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Contains("start type is disabled", console.Text, StringComparison.Ordinal);
        Assert.Contains("refuses to start", console.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Repair finalized", console.Text, StringComparison.Ordinal);

        _env.FailRestore = false;
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).Finalize(Root, Backup, [])); // can be run again
        Assert.Equal("auto", _env.StartType);
    }

    [Fact]
    public void Finalize_refuses_a_damaged_backup_copy()
    {
        WithoutPluginContent(); // a manual fix may change access only: contents must still match the manifest
        InterruptAndUndo();
        FixByHand();
        var copy = _fs.Nodes.Keys.Single(k => k.StartsWith(Backup + "/data/", StringComparison.Ordinal) && k.EndsWith("config.json", StringComparison.Ordinal));
        _fs.Nodes[copy].Content = Encoding.UTF8.GetBytes("""{"pcId":"z"}""");
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Finalize(Root, Backup, []));
        Assert.Contains("backup: data/config.json differs from the manifest".Replace("data/", "", StringComparison.Ordinal), console.Text, StringComparison.Ordinal);
        Assert.Equal("disabled", _env.StartType);
    }

    [Fact]
    public void Finalize_refuses_when_preconditions_fail()
    {
        WithoutPluginContent(); // a manual fix may change access only: contents must still match the manifest
        InterruptAndUndo();
        FixByHand();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Finalize(Root, Backup, ["The Nexus PC service is not stopped (state 4)."]));
        _env.FailuresAt = _ => ["Sessions: 2 signed-in Windows sessions."];
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Finalize(Root, Backup, []));
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(RepairJournal.Restored, State(Backup));
    }

    [Fact]
    public void Finalize_of_a_completed_repair_changes_nothing()
    {
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair().Repair(Root, []));
        var changes = _env.StartTypeChanges.Count;
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).Finalize(Root, Backup, []));
        Assert.Equal(changes, _env.StartTypeChanges.Count);
    }

    [Fact]
    public void New_repair_after_restore_acl_takes_over_and_finishes_both()
    {
        InterruptAndUndo();
        var undone = Backup;
        _time.Now = _time.Now.AddMinutes(5);
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(console).Repair(Root, []));
        var newer = Backups.Single(b => b != undone);
        Assert.Equal(RepairJournal.Completed, State(newer));
        Assert.Equal(RepairJournal.Superseded, State(undone));
        Assert.Equal("auto", _env.StartType); // the saved original, not the "disabled" the undone repair left
        Assert.Empty(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.True(DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids).Trusted);
    }

    [Fact]
    public void New_repair_on_a_folder_fixed_by_hand_points_to_finalize()
    {
        WithoutPluginContent(); // a manual fix may change access only: contents must still match the manifest
        InterruptAndUndo();
        FixByHand();
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Contains("datadir finalize", console.Text, StringComparison.Ordinal);
        Assert.Equal("disabled", _env.StartType);
    }

    [Fact]
    public void Continue_after_restore_acl_explains_how_to_finish()
    {
        InterruptAndUndo();
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Continue(Root, Backup, []));
        Assert.Contains("datadir finalize", console.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ QA-T4-R3-2: disabling fails

    [Fact]
    public void Failure_to_disable_the_service_changes_nothing_and_says_so_truthfully()
    {
        _env.FailDisable = true;
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Equal(0, _fs.Renames);
        Assert.Equal("auto", _env.StartType);
        Assert.Contains("could not be disabled", console.Text, StringComparison.Ordinal);
        Assert.Contains("Service start type now: auto", console.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("is disabled for the repair", console.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("stays disabled", console.Text, StringComparison.Ordinal);
        Assert.Empty(Backups); // abandoned: kept as .incomplete, nothing blocks the service
        Assert.Contains(_fs.Nodes.Keys, k => k.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal));
        Assert.Empty(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
    }

    [Fact]
    public void Failure_to_disable_on_continue_reports_the_real_start_type_with_code_3()
    {
        _fs.CrashAfterSecurityChanges = 2;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        var path = Backup + "/" + DataDirectoryRepair.JournalName;
        var json = JsonNode.Parse(_fs.ReadAllText(path)!)!.AsObject();
        json["serviceDisabled"] = false; // say someone re-enabled it by hand
        _fs.Nodes[path].Content = Encoding.UTF8.GetBytes(json.ToJsonString());
        _env.StartType = "demand";
        _env.FailDisable = true;
        var changes = _fs.SecurityChanges;
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Continue(Root, Backup, []));
        Assert.Equal(changes, _fs.SecurityChanges); // no further change of access
        Assert.Contains("Service start type now: demand", console.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("is disabled for the repair", console.Text, StringComparison.Ordinal);
        Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
    }

    [Fact]
    public void Stopped_repair_reports_the_actual_start_type()
    {
        _env.FailuresAt = n => n == 3 ? ["simulated"] : [];
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Repair(Root, []));
        Assert.Contains("its start type is now: disabled", console.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ round 4b (QA-T4-R4-1 … -3)

    [Fact]
    public void Finalize_refuses_while_another_repair_is_unfinished()
    {
        WithoutPluginContent();
        InterruptAndUndo();
        var undone = Backup;
        _time.Now = _time.Now.AddMinutes(5);
        var calls = 0;
        _env.FailuresAt = _ => ++calls == 3 ? ["simulated: a second session signed in"] : [];
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair().Repair(Root, []));
        _env.FailuresAt = null;
        var newer = Backups.Single(b => b != undone);
        FixByHand();
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Finalize(Root, undone, []));
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(RepairJournal.Restored, State(undone));
        Assert.Equal(RepairJournal.Stopped, State(newer));
        Assert.Contains(newer, console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_of_the_repair_that_took_over_may_finish_both()
    {
        WithoutPluginContent();
        InterruptAndUndo();
        var undone = Backup;
        _time.Now = _time.Now.AddMinutes(5);
        var calls = 0;
        _env.FailuresAt = _ => ++calls == 3 ? ["simulated"] : [];
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair().Repair(Root, []));
        _env.FailuresAt = null;
        var newer = Backups.Single(b => b != undone);
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).Continue(Root, newer, [])); // the undone one is its own
        Assert.Equal(RepairJournal.Completed, State(newer));
        Assert.Equal(RepairJournal.Superseded, State(undone));
        Assert.Equal("auto", _env.StartType);
    }

    /// <summary>A second, independent unfinished repair next to the first (a trusted copy of its backup).</summary>
    private string CloneBackup(string source)
    {
        var clone = source + "-other";
        foreach (var (key, node) in _fs.Nodes.Where(n => n.Key == source || n.Key.StartsWith(source + "/", StringComparison.Ordinal)).ToList())
        {
            _fs.Nodes[clone + key[source.Length..]] = new FakeDataFileSystem.Node
            {
                Kind = node.Kind, Id = _fs.NewId(), Owner = node.Owner, Protected = node.Protected, Aces = [.. node.Aces], Content = [.. node.Content],
            };
        }
        return clone;
    }

    [Fact]
    public void Continue_does_not_complete_while_an_unrelated_repair_is_unfinished()
    {
        _fs.CrashAfterSecurityChanges = 2;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        var first = Backup;
        var other = CloneBackup(first);
        Assert.Equal(2, Repair().FindUnfinished(Root).Count(r => !r.Foreign));
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Continue(Root, first, []));
        Assert.Equal("disabled", _env.StartType);
        Assert.NotEqual(RepairJournal.Completed, State(first));
        Assert.Contains(other, console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_rechecks_preconditions_right_before_the_start_type_goes_back()
    {
        WithoutPluginContent();
        InterruptAndUndo();
        FixByHand();
        var calls = 0;
        // 1: before the checks; 2: right before the start type is put back (Complete).
        _env.FailuresAt = _ => ++calls == 2 ? ["Sessions: 2 signed-in Windows sessions."] : [];
        var console = new ScriptedConsole();
        Assert.NotEqual(DataDirectoryRepair.ExitOk, Repair(console).Finalize(Root, Backup, []));
        Assert.Equal(2, calls);
        Assert.Equal("disabled", _env.StartType);
        Assert.Equal(RepairJournal.Restored, State(Backup));
        Assert.Contains("preconditions no longer hold", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Continue_rechecks_preconditions_right_before_the_start_type_goes_back()
    {
        _fs.CrashAfterSecurityChanges = 2;
        Assert.Throws<SimulatedCrashException>(() => Repair().Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;
        // The rename of the plugins folder is the last step; the next check is the one before the start type.
        _env.FailuresAt = _ => _fs.Renames == 1 ? ["Sessions: 2 signed-in Windows sessions."] : [];
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Continue(Root, Backup, []));
        Assert.Equal(1, _fs.Renames); // every step done
        Assert.Equal("disabled", _env.StartType);
        Assert.NotEqual(RepairJournal.Completed, State(Backup));
        Assert.Contains("preconditions no longer hold", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_disable_with_a_backup_that_cannot_be_renamed_says_the_service_is_blocked()
    {
        _env.FailDisable = true;
        _fs.FailMoveDirectory = true;
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Repair(Root, []));
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Equal("auto", _env.StartType);
        Assert.Single(DataDirectoryRepair.StartCheck(_fs, Root, DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Contains("refuses to start", console.Text, StringComparison.Ordinal);
        Assert.Contains("datadir repair --continue \"" + Backup + "\"", console.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ arguments

    [Theory]
    [InlineData("finalize B", "finalize", "B")]
    [InlineData("finalize B --data-dir E:\\ProgramData\\NexusPC", "finalize", "B")]
    public void Finalize_is_parsed(string line, string verb, string backup)
    {
        var parsed = DataDirectoryCommandLine.Parse(line.Split(' '));
        Assert.Equal(verb, parsed?.Verb);
        Assert.Equal(backup, parsed?.Backup);
        Assert.Null(DataDirectoryCommandLine.Parse(["finalize"]));
        var options = SetupOptions.Parse(["--datadir-finalize", "B"]);
        Assert.Equal("finalize", options?.DataDirectory?.Verb);
        Assert.Null(SetupOptions.Parse(["--datadir-finalize"]));
    }
}
