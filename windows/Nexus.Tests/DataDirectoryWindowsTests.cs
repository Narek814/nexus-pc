using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Nexus.Agent.Storage;
using Nexus.Core.Storage;
using Nexus.Setup;
using Nexus.Windows;
using Xunit.Abstractions;

namespace Nexus.Tests;

/// <summary>T4 Windows tests change real NTFS security and process privileges: never in parallel with each other or other tests.</summary>
[CollectionDefinition(nameof(DataDirectoryWindowsSerial), DisableParallelization = true)]
public sealed class DataDirectoryWindowsSerial;

/// <summary>
/// The real handle-based file layer on NTFS. Runs only on Windows (CI windows-latest, elevated). Every
/// object is made by the test under %TEMP%; nothing outside it is read or changed. No real service is touched.
/// </summary>
[Collection(nameof(DataDirectoryWindowsSerial))]
public sealed class DataDirectoryWindowsTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _cleanup = [];
    private DataRepairEnvironment.PrivilegeScope? _privileges;

    /// <summary>Windows and elevated; on CI (GITHUB_ACTIONS) a missing elevation fails instead of skipping.</summary>
    private bool Ready()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Not Windows: NTFS owners, ACLs, links and hard links are checked by CI windows-latest.");
            return false;
        }
        if (!Elevated())
        {
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "CI must run these tests elevated.");
            output.WriteLine("Not elevated: skipped locally.");
            return false;
        }
        _privileges ??= DataRepairEnvironment.EnablePrivileges(DataRepairEnvironment.RepairPrivileges);
        Assert.Empty(_privileges.Missing);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private string NewRoot()
    {
        // The parent is where backups go: it must pass the parent check (owner Administrators, protected).
        var parent = Path.Combine(Path.GetTempPath(), "nexus-t4-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, DataDirectoryFileSystem.TryCreateProtectedDirectory(parent, withOwner: true));
        _cleanup.Add(parent);
        var root = Path.Combine(parent, "NexusPC");
        AgentPaths.CreateProtectedIfMissing(root);
        return root;
    }

    [SupportedOSPlatform("windows")]
    private static RawSecurityDescriptor Descriptor(string path)
    {
        using var obj = new DataDirectoryFileSystem().Open(path, DataAccess.Inspect);
        return new RawSecurityDescriptor(obj.State.SecurityDescriptor!);
    }

    [SupportedOSPlatform("windows")]
    private static string Sddl(string path)
    {
        using var obj = new DataDirectoryFileSystem().Open(path, DataAccess.Inspect);
        return obj.State.SecurityDescriptor!;
    }

    [SupportedOSPlatform("windows")]
    private static void Grant(string path, WellKnownSidType sid, bool directory)
    {
        var rule = new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.Modify, AccessControlType.Allow);
        if (directory)
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    private static void Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{exe} {string.Join(' ', args)}: {text}");
    }

    [Fact]
    [SupportedOSPlatform("windows")] // checked at run time by Ready(); lambdas below use Windows-only types
    public void New_folder_is_owned_by_administrators_with_a_protected_two_entry_dacl()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        var sd = Descriptor(root);
        Assert.Equal(DataDirectoryPolicy.Administrators, sd.Owner!.Value);
        Assert.True((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
        Assert.Equal(2, sd.DiscretionaryAcl!.Count);
        var sids = sd.DiscretionaryAcl.Cast<CommonAce>().Select(a => a.SecurityIdentifier.Value).Order().ToList();
        Assert.Equal([DataDirectoryPolicy.LocalSystem, DataDirectoryPolicy.Administrators], sids);
        Assert.Empty(AgentPaths.CheckDirectory(root, DataDirectoryPolicy.TrustedSids));
        // An existing folder is left as it is (and only checked), even when its access differs.
        Grant(root, WellKnownSidType.BuiltinUsersSid, directory: true);
        var changed = Sddl(root);
        AgentPaths.CreateProtectedIfMissing(root);
        Assert.Equal(changed, Sddl(root));
        Assert.Contains(AgentPaths.CheckDirectory(root, DataDirectoryPolicy.TrustedSids), v => v.Kind == DataViolationKind.UntrustedAccess);
    }

    [Fact]
    [SupportedOSPlatform("windows")] // checked at run time by Ready(); lambdas below use Windows-only types
    public void Other_owner_or_extra_access_entry_is_reported()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        var file = Path.Combine(root, "devices.json");
        File.WriteAllText(file, "[]");
        Grant(file, WellKnownSidType.BuiltinUsersSid, directory: false);
        var sub = Path.Combine(root, "OpenRGB");
        Directory.CreateDirectory(sub);
        var me = WindowsIdentity.GetCurrent().User!;
        var info = new DirectoryInfo(sub);
        var security = info.GetAccessControl();
        security.SetOwner(me);
        info.SetAccessControl(security);

        var violations = AgentPaths.CheckDirectory(root, DataDirectoryPolicy.TrustedSids);
        Assert.Contains(violations, v => v.RelativePath == "devices.json" && v.Kind == DataViolationKind.UntrustedAccess && v.Detail.Contains("S-1-5-32-545", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.RelativePath == "OpenRGB" && v.Kind == DataViolationKind.UntrustedOwner && v.Detail.Contains(me.Value, StringComparison.Ordinal));
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, new DataDirectoryRepair(new DataDirectoryFileSystem(), console, TimeProvider.System).Check(root));
        Assert.Contains("devices.json", console.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("junction-root")]
    [InlineData("junction-deep")]
    [InlineData("symlink-dir")]
    [InlineData("symlink-file")]
    public void Links_are_refused_and_their_targets_are_never_changed(string kind)
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "secret.txt");
        File.WriteAllText(outsideFile, "not ours");
        Grant(outside, WellKnownSidType.BuiltinUsersSid, directory: true);
        Directory.CreateDirectory(Path.Combine(root, "OpenRGB", "deep"));
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");
        var before = (Sddl(outside), Sddl(outsideFile));

        switch (kind)
        {
            case "junction-root":
                Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(root, "link"), outside);
                break;
            case "junction-deep":
                Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(root, "OpenRGB", "deep", "link"), outside);
                break;
            case "symlink-dir":
                Directory.CreateSymbolicLink(Path.Combine(root, "OpenRGB", "plugins"), outside);
                break;
            default:
                File.CreateSymbolicLink(Path.Combine(root, "devices.json"), outsideFile);
                break;
        }

        Assert.Contains(AgentPaths.CheckDirectory(root, DataDirectoryPolicy.TrustedSids), v => v.Kind == DataViolationKind.ReparsePoint);
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, new DataDirectoryRepair(new DataDirectoryFileSystem(), console, TimeProvider.System).Repair(root, []));
        Assert.Equal(before, (Sddl(outside), Sddl(outsideFile)));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(root)!, "NexusPC.backup-*"));
    }

    [Fact]
    public void File_with_two_hard_links_is_refused_and_the_second_link_is_not_changed()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "elsewhere.txt");
        File.WriteAllText(outside, "shared");
        var inside = Path.Combine(root, "rgb.json");
        Run("fsutil.exe", "hardlink", "create", inside, outside);
        var before = Sddl(outside);

        Assert.Contains(AgentPaths.CheckDirectory(root, DataDirectoryPolicy.TrustedSids), v => v.Kind == DataViolationKind.HardLinks && v.RelativePath == "rgb.json");
        Assert.Equal(DataDirectoryRepair.ExitRefused, new DataDirectoryRepair(new DataDirectoryFileSystem(), new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), TimeProvider.System).Repair(root, []));
        Assert.Equal(before, Sddl(outside));
    }

    [Fact]
    public void Full_repair_then_restore_acl_on_a_test_folder()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "config.json"), """{"pcId":"00000000-0000-0000-0000-000000000001"}""");
        File.WriteAllText(Path.Combine(root, "devices.json"), "[]");
        File.WriteAllBytes(Path.Combine(root, "audit.log"), RandomNumberGenerator.GetBytes(300_000));
        Directory.CreateDirectory(Path.Combine(root, "OpenRGB", "plugins"));
        File.WriteAllText(Path.Combine(root, "OpenRGB", "OpenRGB.json"), "{}");
        File.WriteAllText(Path.Combine(root, "OpenRGB", "plugins", "evil.dll"), "MZ");
        Grant(Path.Combine(root, "devices.json"), WellKnownSidType.BuiltinUsersSid, directory: false);
        Grant(Path.Combine(root, "OpenRGB"), WellKnownSidType.AuthenticatedUserSid, directory: true);
        var hashes = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace(@"\plugins\", @"\plugins*\", StringComparison.Ordinal), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));
        var original = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Append(root)
            .ToDictionary(p => p, Sddl);
        Assert.NotEmpty(AgentPaths.CheckDirectory(root, DataDirectoryPolicy.TrustedSids));

        var fs = new DataDirectoryFileSystem();
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        var code = new DataDirectoryRepair(fs, console, TimeProvider.System).Repair(root, []);
        output.WriteLine(console.Text);
        Assert.Equal(DataDirectoryRepair.ExitOk, code);
        var backup = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(root)!, "NexusPC.backup-*"));
        _cleanup.Add(backup);

        // The result passes the check, contents equal the manifest, plugins are renamed, not deleted.
        Assert.Empty(AgentPaths.CheckDirectory(root, DataDirectoryPolicy.TrustedSids));
        Assert.False(Directory.Exists(Path.Combine(root, "OpenRGB", "plugins")));
        var disabled = Assert.Single(Directory.GetDirectories(Path.Combine(root, "OpenRGB"), "plugins.disabled-*"));
        Assert.True(File.Exists(Path.Combine(disabled, "evil.dll")));
        var now = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => System.Text.RegularExpressions.Regex.Replace(Path.GetRelativePath(root, f), @"\\plugins\.disabled-[0-9-]+\\", @"\plugins*\"), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));
        Assert.Equal(hashes.OrderBy(p => p.Key), now.OrderBy(p => p.Key));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(backup, DataDirectoryRepair.ManifestName)));
        foreach (var entry in manifest.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "file"))
        {
            var copy = Path.Combine(backup, DataDirectoryRepair.DataFolder, entry.GetProperty("path").GetString()!);
            Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(copy))));
        }
        var rootSd = Descriptor(root);
        Assert.Equal(DataDirectoryPolicy.Administrators, rootSd.Owner!.Value);
        Assert.True((rootSd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
        Assert.True(new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected);
        var backupSd = Descriptor(backup);
        Assert.Equal(DataDirectoryPolicy.Administrators, backupSd.Owner!.Value);
        Assert.True((backupSd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);

        // restore-acl: every object gets its original descriptor back; contents untouched.
        var restore = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, restore, TimeProvider.System).RestoreAcl(root, backup, []));
        foreach (var (path, sddl) in original)
        {
            var current = path.Contains(@"\plugins", StringComparison.Ordinal) ? path.Replace(Path.Combine(root, "OpenRGB", "plugins"), disabled, StringComparison.Ordinal) : path;
            Assert.True(sddl == Sddl(current), $"{path}: {sddl} != {Sddl(current)}");
        }
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void Service_start_refuses_an_untrusted_folder_and_changes_nothing()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        var devices = Path.Combine(root, "devices.json");
        File.WriteAllText(devices, "[]");
        Grant(devices, WellKnownSidType.BuiltinUsersSid, directory: false);
        var before = (Sddl(root), Sddl(devices), File.ReadAllText(devices));
        // The production trust set (no override): exactly SYSTEM, Administrators, TrustedInstaller.
        Assert.Throws<DataDirectoryUntrustedException>(() => AgentPaths.EnsureSecureDirectory(root, null));
        Assert.Equal(before, (Sddl(root), Sddl(devices), File.ReadAllText(devices)));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData("untrusted")]
    [InlineData("junction")]
    [SupportedOSPlatform("windows")] // checked at run time by Ready(); the local function uses Windows-only calls
    public void Installer_stops_before_writing_anything_on_an_untrusted_folder(string kind)
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");
        if (kind == "untrusted")
        {
            Grant(root, WellKnownSidType.BuiltinUsersSid, directory: true);
        }
        else
        {
            var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside");
            Directory.CreateDirectory(outside);
            Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(root, "OpenRGB"), outside);
        }
        string Tree() => string.Join("\n", Directory.GetFileSystemEntries(Path.GetDirectoryName(root)!, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Order(StringComparer.Ordinal).Select(p => p + "|" + Sddl(p) + "|" + (File.Exists(p) ? new FileInfo(p).Length : 0)));
        var before = Tree();
        using var output = new StringWriter();
        Assert.Equal(SetupPlan.ExitDataDirectoryUntrusted, Installer.DataDirectoryGate(root, output));
        Assert.Equal(before, Tree());
        Assert.Contains("--datadir-repair", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_folder_is_created_protected_by_the_installer_gate()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var parent = Path.GetDirectoryName(NewRoot())!;
        var fresh = Path.Combine(parent, "second", "NexusPC");
        Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
        using var output = new StringWriter();
        Assert.Null(Installer.DataDirectoryGate(fresh, output));
        Assert.True(new DirectoryInfo(fresh).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal(DataDirectoryPolicy.Administrators, Descriptor(fresh).Owner!.Value);
        Assert.Null(Installer.DataDirectoryGate(fresh, output)); // exists and trusted: accepted, unchanged
    }

    [Fact]
    public void Restore_comparison_reads_the_real_descriptor_and_tells_a_lost_ai_flag_from_other_changes()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        string? read;
        using (var obj = new DataDirectoryFileSystem().Open(root, DataAccess.Inspect))
        {
            read = obj.ReadSecurityDescriptor();
            Assert.Equal(obj.State.SecurityDescriptor, read);
        }
        Assert.Equal(RestoreOutcome.Exact, DataDirectoryPolicy.CompareRestored(read!, Sddl(root)));
        // The same descriptor in the form Windows writes it, with and without SE_DACL_AUTO_INHERITED.
        var withAi = new RawSecurityDescriptor(read!);
        withAi.SetFlags(withAi.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        var withoutAi = new RawSecurityDescriptor(read!);
        withoutAi.SetFlags(withoutAi.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
        Assert.Equal(RestoreOutcome.AutoInheritedLost, DataDirectoryPolicy.CompareRestored(withAi.GetSddlForm(sections), withoutAi.GetSddlForm(sections)));
        var otherOwner = new RawSecurityDescriptor(withoutAi.GetSddlForm(sections)) { Owner = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) };
        Assert.Equal(RestoreOutcome.Different, DataDirectoryPolicy.CompareRestored(withAi.GetSddlForm(sections), otherOwner.GetSddlForm(sections)));
    }

    [Fact]
    public void Installer_gate_and_uninstall_refuse_when_the_check_privileges_cannot_be_enabled()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        // A privilege name Windows does not know: it lands in Missing; nothing in the system is changed.
        IReadOnlyList<string> unknown = ["SeNexusTestNoSuchPrivilege"];
        var root = NewRoot(); // exists and trusted: only the privileges decide
        var before = Sddl(root);
        using var output = new StringWriter();
        Assert.Equal(SetupPlan.ExitDataDirectoryUntrusted, Installer.DataDirectoryGate(root, output, unknown));
        Assert.Contains("проверка папки данных Nexus PC не выполнена", output.ToString(), StringComparison.Ordinal);
        Assert.Null(Installer.DataDirectoryGate(root, new StringWriter())); // with the real privileges the same folder passes
        var refusal = Installer.UnfinishedRepairBlocksUninstall(root, unknown);
        Assert.NotNull(refusal);
        Assert.StartsWith("Удаление остановлено, ничего не изменено", refusal, StringComparison.Ordinal);
        Assert.Null(Installer.UnfinishedRepairBlocksUninstall(root));
        Assert.Equal(before, Sddl(root));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void Service_start_refuses_while_a_repair_is_unfinished_until_it_is_continued()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");
        File.WriteAllText(Path.Combine(root, "devices.json"), "[]");
        Grant(Path.Combine(root, "devices.json"), WellKnownSidType.BuiltinUsersSid, directory: false);
        var fs = new DataDirectoryFileSystem();
        // No real service is touched: the start type lives in this double; a precondition fails mid-way.
        var environment = new FakeRepairEnvironment { FailuresAt = n => n == 3 ? ["simulated: a second session signed in"] : [] };
        Assert.Equal(DataDirectoryRepair.ExitStopped, new DataDirectoryRepair(fs, new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), TimeProvider.System, environment).Repair(root, []));
        Assert.Equal("disabled", environment.StartType);
        var backup = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(root)!, "NexusPC.backup-*"));
        _cleanup.Add(backup);

        var refused = Assert.Throws<DataDirectoryUntrustedException>(() => AgentPaths.EnsureSecureDirectory(root, null));
        Assert.Contains("--continue", refused.Message, StringComparison.Ordinal);

        environment.FailuresAt = null;
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, new ScriptedConsole(), TimeProvider.System, environment).Continue(root, backup, []));
        Assert.Equal("auto", environment.StartType);
        AgentPaths.EnsureSecureDirectory(root, null); // completed and verified: the start check passes again
    }

    [Fact]
    public void Restore_acl_then_finalize_on_ntfs_keeps_the_service_blocked_until_every_check_passes()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");
        File.WriteAllText(Path.Combine(root, "devices.json"), "[]");
        Grant(Path.Combine(root, "devices.json"), WellKnownSidType.BuiltinUsersSid, directory: false);
        var fs = new DataDirectoryFileSystem();
        var environment = new FakeRepairEnvironment { FailuresAt = n => n == 3 ? ["simulated: a second session signed in"] : [] };
        Assert.Equal(DataDirectoryRepair.ExitStopped, new DataDirectoryRepair(fs, new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), TimeProvider.System, environment).Repair(root, []));
        environment.FailuresAt = null;
        var backup = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(root)!, "NexusPC.backup-*"));
        _cleanup.Add(backup);
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, new ScriptedConsole(), TimeProvider.System, environment).RestoreAcl(root, backup, []));
        Assert.Equal("disabled", environment.StartType);

        var refused = Assert.Throws<DataDirectoryUntrustedException>(() => AgentPaths.EnsureSecureDirectory(root, null));
        Assert.Contains("datadir finalize", refused.Message, StringComparison.Ordinal);
        Assert.Equal(DataDirectoryRepair.ExitRefused, new DataDirectoryRepair(fs, new ScriptedConsole(), TimeProvider.System, environment).Finalize(root, backup, []));
        Assert.Equal("disabled", environment.StartType);

        // The administrator's own fix, by handle: protected root, only inherited entries below it.
        foreach (var path in Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Append(root))
        {
            using var obj = fs.Open(path, DataAccess.ChangeSecurity);
            obj.SetSecurity(path == root ? new DataSecurity.ProtectedRoot() : new DataSecurity.InheritedFromRoot(obj.State.Kind == DataObjectKind.Directory));
        }
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, console, TimeProvider.System, environment).Finalize(root, backup, []));
        output.WriteLine(console.Text);
        Assert.Equal("auto", environment.StartType);
        AgentPaths.EnsureSecureDirectory(root, null); // finalized and verified: the start check passes
    }

    [Fact]
    public void Uninstall_is_refused_while_a_repair_is_unfinished_and_a_tampered_backup_still_blocks()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");
        File.WriteAllText(Path.Combine(root, "devices.json"), "[]");
        Grant(Path.Combine(root, "devices.json"), WellKnownSidType.BuiltinUsersSid, directory: false);
        var fs = new DataDirectoryFileSystem();
        var environment = new FakeRepairEnvironment { FailuresAt = n => n == 3 ? ["simulated"] : [] };
        Assert.Equal(DataDirectoryRepair.ExitStopped, new DataDirectoryRepair(fs, new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), TimeProvider.System, environment).Repair(root, []));
        var backup = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(root)!, "NexusPC.backup-*"));
        _cleanup.Add(backup);

        var refusal = Installer.UnfinishedRepairBlocksUninstall(root);
        Assert.NotNull(refusal);
        Assert.Contains("--datadir-repair --continue", refusal, StringComparison.Ordinal);

        // What Explorer's "Continue" does to a folder the administrator could not open: an ACE for that account.
        Grant(backup, WellKnownSidType.AuthenticatedUserSid, directory: true);
        Assert.Throws<DataDirectoryUntrustedException>(() => AgentPaths.EnsureSecureDirectory(root, null));
        using var output = new StringWriter();
        Assert.Equal(SetupPlan.ExitDataDirectoryUntrusted, Installer.DataDirectoryGate(root, output));
        Assert.NotNull(Installer.UnfinishedRepairBlocksUninstall(root));
    }

    /// <summary>Runs <paramref name="action"/> on a new thread (a mutex belongs to its thread) and waits.</summary>
    private static T OnThread<T>(Func<T> action)
    {
        T result = default!;
        var thread = new Thread(() => result = action());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    [SupportedOSPlatform("windows")] // checked at run time by Ready(); lambdas below use Windows-only types
    public void Protected_lock_excludes_a_second_holder_and_ignores_a_squatted_global_name()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        // Someone took the old name with an access list that refuses everybody: it no longer matters.
        var denyAll = new System.Security.AccessControl.MutexSecurity();
        denyAll.SetAccessRuleProtection(true, false);
        using var squatted = System.Threading.MutexAcl.Create(false, @"Global\NexusPC.DataDir", out _, denyAll);

        var locks = new ProtectedDataDirectoryLock();
        Assert.Equal(LockOutcome.Acquired, locks.TryAcquire(out var held, out var failure));
        Assert.Null(failure);
        using (held)
        {
            var (second, secondFailure) = OnThread(() =>
            {
                var outcome = new ProtectedDataDirectoryLock().TryAcquire(out var other, out var refusal);
                other?.Dispose();
                return (outcome, refusal);
            });
            Assert.True(second == LockOutcome.Busy, $"expected Busy, got {second}: {secondFailure ?? "(no failure text)"}");

            // The mutex in the private namespace: SYSTEM and Administrators only, nothing inherited.
            using var opened = System.Threading.MutexAcl.OpenExisting(ProtectedDataDirectoryLock.MutexName, System.Security.AccessControl.MutexRights.ReadPermissions);
            var security = opened.GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<System.Security.AccessControl.MutexAccessRule>()
                .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Order().ToList();
            Assert.Equal([DataDirectoryPolicy.LocalSystem, DataDirectoryPolicy.Administrators], sids);
        }
        Assert.Equal(LockOutcome.Acquired, OnThread(() =>
        {
            var outcome = new ProtectedDataDirectoryLock().TryAcquire(out var again, out _);
            again?.Dispose();
            return outcome;
        }));
    }

    /// <summary>Exit code and output of a System32 tool; never asserts (used for cleanup).</summary>
    private static (int Code, string Text) TryRun(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, text);
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }
            Thread.Sleep(200);
        }
        return File.Exists(path);
    }

    [Fact]
    [SupportedOSPlatform("windows")] // checked at run time by Ready(); lambdas below use Windows-only types
    public void Protected_lock_excludes_a_holder_in_another_session()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        // It creates (and always deletes) a SYSTEM scheduled task: only on a GitHub-hosted runner, which is a
        // fresh machine per job — never on a developer's PC or a self-hosted runner (GITHUB_ACTIONS alone is set there too).
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            output.WriteLine("Not run: creates a temporary SYSTEM scheduled task, so it runs only on a GitHub-hosted runner (GITHUB_ACTIONS=true, RUNNER_ENVIRONMENT=github-hosted).");
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        var task = "NexusPC-T4-LockTest-" + id;
        // Everything SYSTEM runs or reads lives in a folder only SYSTEM and Administrators may change
        // (protected DACL from its creation): a copy of the test build, verified by SHA-256, and the signal files.
        // Remaining Low risk (CI only, SA-T4-R8-A): the build folder the copy is taken from is writable by the runner
        // account until the copy is made; the SHA-256 proves the copy equals that folder, not that the folder was
        // never changed. Closing that would need a workflow step (protect or sign the build output), and ci.yml is
        // not to be changed. On a GitHub-hosted runner the job itself is the only writer, so this is accepted.
        var folder = Path.Combine(Path.GetTempPath(), "nexus-t4-lock-" + id);
        Assert.Equal(0, DataDirectoryFileSystem.TryCreateProtectedDirectory(folder, withOwner: true));
        _cleanup.Add(folder);
        var helper = Path.Combine(folder, "helper");
        Directory.CreateDirectory(helper);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(helper, Path.GetRelativePath(AppContext.BaseDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(copy))));
        }
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        Assert.True(File.Exists(dotnet), "dotnet.exe not found at " + dotnet);
        var assembly = Path.Combine(helper, Path.GetFileName(typeof(TestHelperProgram).Assembly.Location));
        // Task XML: locale-independent, a one-time trigger in the past (it never fires by itself, and never again),
        // run by hand once, as SYSTEM, at most 5 minutes; the helper releases by itself after 90 seconds.
        var xml = Path.Combine(folder, "task.xml");
        File.WriteAllText(xml, $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><TimeTrigger><StartBoundary>2000-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <StartWhenAvailable>false</StartWhenAvailable>
                <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec>
                <Command>{System.Security.SecurityElement.Escape(dotnet)}</Command>
                <Arguments>{System.Security.SecurityElement.Escape($"exec \"{assembly}\" --hold-datadir-lock \"{folder}\" 90")}</Arguments>
              </Exec></Actions>
            </Task>
            """, System.Text.Encoding.Unicode);

        Exception? primary = null;
        try
        {
            Run("schtasks.exe", "/Create", "/TN", task, "/XML", xml, "/F");
            Run("schtasks.exe", "/Run", "/TN", task);
            Assert.True(WaitForFile(Path.Combine(folder, "acquired"), TimeSpan.FromSeconds(90)) || File.Exists(Path.Combine(folder, "failed")),
                "the SYSTEM helper did not start");
            Assert.False(File.Exists(Path.Combine(folder, "failed")), "SYSTEM helper: " + (File.Exists(Path.Combine(folder, "failed")) ? File.ReadAllText(Path.Combine(folder, "failed")) : ""));
            var helperSide = File.ReadAllText(Path.Combine(folder, "acquired"));
            var ourSession = Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            output.WriteLine("Helper: " + helperSide + "; this test: " + Environment.UserName + " session " + ourSession);
            if (helperSide.EndsWith(" session " + ourSession, StringComparison.Ordinal))
            {
                // Still a real exclusion between two processes and two accounts (SYSTEM and the runner account).
                output.WriteLine("NOTE: both sides run in session " + ourSession + ": exclusion BETWEEN SESSIONS is not verified by this run (only between processes and accounts).");
            }

            Assert.Equal(LockOutcome.Busy, OnThread(() => DisposeAndReturn(new ProtectedDataDirectoryLock(), out _)));

            File.WriteAllText(Path.Combine(folder, "release"), "");
            Assert.True(WaitForFile(Path.Combine(folder, "released"), TimeSpan.FromSeconds(30)), "the SYSTEM helper did not release the lock");
            var acquired = LockOutcome.Unavailable;
            for (var attempt = 0; attempt < 50 && acquired != LockOutcome.Acquired; attempt++)
            {
                acquired = OnThread(() => DisposeAndReturn(new ProtectedDataDirectoryLock(), out _));
                if (acquired != LockOutcome.Acquired)
                {
                    Thread.Sleep(100); // the helper process may still be exiting
                }
            }
            Assert.Equal(LockOutcome.Acquired, acquired);
        }
#pragma warning disable CA1031 // the original failure is kept and rethrown after the cleanup below
        catch (Exception ex)
#pragma warning restore CA1031
        {
            primary = ex;
        }

        // Cleanup always runs; its own problems never hide the original failure.
        var cleanup = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(folder, "release"), "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cleanup.Add("release signal: " + ex.Message);
        }
        var ended = TryRun("schtasks.exe", "/End", "/TN", task);
        var deleted = TryRun("schtasks.exe", "/Delete", "/TN", task, "/F");
        output.WriteLine($"Cleanup: end {ended.Code}, delete {deleted.Code} {deleted.Text.Trim()}");
        if (TryRun("schtasks.exe", "/Query", "/TN", task).Code == 0)
        {
            cleanup.Add($"the task {task} still exists after /Delete ({deleted.Text.Trim()})");
        }
        if (primary is not null)
        {
            foreach (var problem in cleanup)
            {
                output.WriteLine("Cleanup problem after the failure: " + problem);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
        Assert.True(cleanup.Count == 0, "cleanup failed: " + string.Join("; ", cleanup));
    }

    [Fact]
    [SupportedOSPlatform("windows")] // checked at run time by Ready(); lambdas below use Windows-only types
    public void Lookalike_lock_names_outside_the_private_namespace_are_not_taken_for_the_lock()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        // Objects named like the lock in the ordinary namespaces, held and with an access list that refuses
        // everybody: the protected lock neither uses nor waits for them.
        var denyAll = new System.Security.AccessControl.MutexSecurity();
        denyAll.SetAccessRuleProtection(true, false);
        using var local = System.Threading.MutexAcl.Create(true, @"Local\NexusPC.DataDir", out _, denyAll);
        using var plain = System.Threading.MutexAcl.Create(true, @"Local\NexusPC-DataDir", out _, denyAll);
        Assert.Equal(LockOutcome.Acquired, OnThread(() => DisposeAndReturn(new ProtectedDataDirectoryLock(), out _)));
    }

    [Fact]
    [SupportedOSPlatform("windows")] // checked at run time by Ready(); lambdas below use Windows-only types
    public void Protected_lock_cannot_be_taken_without_an_enabled_administrators_group()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        // The same account with Administrators made deny-only (what a non-elevated or ordinary token has):
        // it may neither create the private namespace nor open it, so it cannot take or squat the lock.
        var outcome = RestrictedToken.RunWithoutAdministrators(() => DisposeAndReturn(new ProtectedDataDirectoryLock(), out _));
        Assert.Equal(LockOutcome.Unavailable, outcome);

        // And while such a token is around, the elevated command still gets the lock.
        Assert.Equal(LockOutcome.Acquired, OnThread(() =>
        {
            var o = new ProtectedDataDirectoryLock().TryAcquire(out var held, out _);
            held?.Dispose();
            return o;
        }));
    }

    [SupportedOSPlatform("windows")]
    private static LockOutcome DisposeAndReturn(ProtectedDataDirectoryLock locks, out string? failure)
    {
        var outcome = locks.TryAcquire(out var held, out failure);
        held?.Dispose();
        return outcome;
    }

    [Fact]
    public void Backup_with_extra_access_is_not_used_by_restore_acl_or_continue()
    {
        if (!Ready() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "devices.json"), "[]");
        Grant(Path.Combine(root, "devices.json"), WellKnownSidType.BuiltinUsersSid, directory: false);
        var fs = new DataDirectoryFileSystem();
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, new ScriptedConsole(DataDirectoryRepair.ConfirmationWord), TimeProvider.System).Repair(root, []));
        var backup = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(root)!, "NexusPC.backup-*"));
        Grant(backup, WellKnownSidType.BuiltinUsersSid, directory: true);
        var after = Sddl(Path.Combine(root, "devices.json"));
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, new DataDirectoryRepair(fs, console, TimeProvider.System).RestoreAcl(root, backup, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, new DataDirectoryRepair(fs, console, TimeProvider.System).Continue(root, backup, []));
        // Exact: both refusals name this backup as tampered (a completed repair whose folder was changed), never as foreign.
        Assert.Equal(2, console.Lines.Count(l => l.StartsWith(backup + ": " + DataDirectoryRepair.TamperedText, StringComparison.Ordinal)));
        Assert.DoesNotContain("foreign folder", console.Text, StringComparison.Ordinal);
        var blocked = Assert.Single(DataDirectoryRepair.StartCheck(fs, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), DataDirectoryPolicy.TrustedSids).Unfinished);
        Assert.Equal(DataDirectoryRepair.Tampered, blocked.State); // its "completed" journal is not believed
        using var gate = new StringWriter();
        Assert.Equal(SetupPlan.ExitDataDirectoryUntrusted, Installer.DataDirectoryGate(root, gate));
        Assert.Equal(after, Sddl(Path.Combine(root, "devices.json")));
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _privileges?.Dispose();
        }
        foreach (var path in _cleanup.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(path, recursive: true); // removes links themselves, never enters them
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine("Cleanup: " + ex.Message);
            }
        }
    }
}
