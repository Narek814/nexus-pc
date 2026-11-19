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

/// <summary>
/// The real handle-based file layer on NTFS. Runs only on Windows (CI windows-latest, elevated). Every
/// object is made by the test under %TEMP%; nothing outside it is read or changed.
/// </summary>
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
        Assert.Contains("foreign folder, not used", console.Text, StringComparison.Ordinal);
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
