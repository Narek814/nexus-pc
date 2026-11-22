using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Nexus.Core.Storage;
using Nexus.Setup;
using Nexus.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Tests;

/// <summary>
/// T5 on real Windows (CI windows-latest). Early return on other systems; tests that need elevation fail on CI
/// (GITHUB_ACTIONS=true) when not elevated instead of passing silently. Test objects only: a folder under %TEMP% and one
/// "NexusPC-t5test-*" folder in Program Files, both removed afterwards.
/// </summary>
[Collection(nameof(DataDirectoryWindowsSerial))]
public sealed class SetupT5WindowsTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _cleanup = [];

    private bool Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Not Windows: checked by CI windows-latest.");
            return false;
        }
        return true;
    }

    private bool Elevated()
    {
        if (!Windows())
        {
            return false;
        }
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "CI must run these tests elevated.");
            output.WriteLine("Not elevated: skipped locally.");
            return false;
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>A new folder under %TEMP%, as its long final path (CI runners use an 8.3 %TEMP%; process image paths are long).</summary>
    [SupportedOSPlatform("windows")]
    private string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nexus-t5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _cleanup.Add(folder);
        using var open = new DataDirectoryFileSystem().Open(folder, DataAccess.Inspect);
        return open.State.FinalPath ?? folder;
    }

    [Fact]
    public void Real_program_files_path_passes_the_checks()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var components = StagingPathPolicy.Components(programFiles);
        Assert.Equal(2, components.Count);
        StagingDirectory.CheckPath(new DataDirectoryFileSystem(), components); // throws StagingRefusedException otherwise
    }

    [Fact]
    [SupportedOSPlatform("windows")] // checked at run time; lambdas use Windows-only types
    public void Junction_on_the_path_is_refused()
    {
        if (!Elevated() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var parent = Path.Combine(Path.GetTempPath(), "nexus-t5-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, DataDirectoryFileSystem.TryCreateProtectedDirectory(parent, withOwner: true));
        _cleanup.Add(parent);
        var target = Path.Combine(parent, "target");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(parent, "link");
        RunCmd("/c", "mklink", "/J", junction, target);
        var fs = new DataDirectoryFileSystem();
        StagingDirectory.CheckPath(fs, [parent, target]); // the same folder without the junction passes
        var refused = Assert.Throws<StagingRefusedException>(() => StagingDirectory.CheckPath(fs, [parent, junction]));
        Assert.StartsWith("reparse point", refused.Message);
        Directory.Delete(junction); // removes the junction only
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Staging_is_administrators_owned_protected_and_at_its_final_path()
    {
        if (!Elevated() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusPC-t5test-" + Guid.NewGuid().ToString("N"));
        _cleanup.Add(root);
        string path;
        using (var staging = StagingDirectory.Create(root, TextWriter.Null))
        {
            path = staging.Path;
            Assert.StartsWith(root + @"\.staging-", path, StringComparison.Ordinal);
            using var open = new DataDirectoryFileSystem().Open(path, DataAccess.Inspect);
            Assert.Equal(DataDirectoryPolicy.Administrators, open.State.OwnerSid);
            Assert.True(open.State.DaclProtected);
            Assert.Equal([DataDirectoryPolicy.LocalSystem, DataDirectoryPolicy.Administrators], open.State.Dacl.Select(a => a.Sid).Order(StringComparer.Ordinal).ToList());
            Assert.Equal(path, open.State.FinalPath, ignoreCase: true);

            // A file written into staging is checked against the manifest through its own handle.
            var bytes = "hello"u8.ToArray();
            var entry = new ManifestEntry("Agent/x.txt", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            using var memory = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using var w = zip.CreateEntry(entry.Path).Open();
                w.Write(bytes);
            }
            memory.Position = 0;
            using var read = new System.IO.Compression.ZipArchive(memory);
            using (var hold = staging.Extract(read, [entry], entry.Path))
            {
                Assert.NotNull(hold);
                Assert.ThrowsAny<IOException>(() => File.OpenWrite(staging.LocalPath(entry.Path)).Dispose());
            }
            Assert.Throws<PayloadRejectedException>(() => staging.Extract(read, [entry with { Path = "Tray/x.txt" }], null));
        }
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Held_file_runs_and_cannot_be_written_deleted_or_renamed_until_the_process_ends()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var folder = TempFolder();
        var exe = Path.Combine(folder, "held-ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        AssertLocked(exe);

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("4");
        start.ArgumentList.Add("127.0.0.1");
        using var process = Process.Start(start)!;
        Assert.True(PawnIoPolicy.SameImage(Authenticode.ImagePath(process.SafeHandle), exe), Authenticode.ImagePath(process.SafeHandle));
        AssertLocked(exe);
        Assert.False(process.HasExited);
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        Assert.Equal(0, process.ExitCode);
        AssertLocked(exe); // still held by the handle after the process ended
        hold.Dispose();
        File.Delete(exe); // and free once the handle is closed
    }

    [SupportedOSPlatform("windows")]
    private static void AssertLocked(string exe)
    {
        Assert.ThrowsAny<IOException>(() => new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
        Assert.ThrowsAny<Exception>(() => File.Delete(exe));
        Assert.ThrowsAny<Exception>(() => File.Move(exe, exe + ".moved"));
        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(exe + ".moved"));
    }

    [Fact]
    public void WinVerifyTrust_accepts_a_signed_runtime_file_and_reads_its_leaf_but_rejects_an_unsigned_one()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var signed = typeof(object).Assembly.Location; // System.Private.CoreLib.dll, Microsoft Authenticode (embedded)
        using (var file = new FileStream(signed, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = Authenticode.Verify(file.SafeFileHandle, signed, checkRevocation: false);
            Assert.Equal(0, result.Code);
            Assert.Matches("^[0-9a-f]{64}$", result.LeafSha256!);
            Assert.Contains("Microsoft", result.LeafSubject!, StringComparison.Ordinal);
            output.WriteLine($"{result.LeafSubject} {result.LeafSha256}");
            var strict = Authenticode.Verify(file.SafeFileHandle, signed, checkRevocation: true);
            Assert.True(strict.Code is 0 or PawnIoPolicy.CryptERevocationOffline or PawnIoPolicy.CryptENoRevocationCheck, $"0x{strict.Code:X8}");
        }
        var unsigned = typeof(PawnIoPolicy).Assembly.Location; // our own build: not signed
        using (var file = new FileStream(unsigned, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = Authenticode.Verify(file.SafeFileHandle, unsigned, checkRevocation: false);
            Assert.NotEqual(0, result.Code);
            Assert.Null(result.LeafSha256);
        }
    }

    [Fact]
    public void Runner_starts_only_the_pinned_signed_file()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        // createdump.exe of the running .NET: an embedded-signed Microsoft executable standing in for PawnIO_setup.exe.
        var source = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "createdump.exe");
        Assert.True(File.Exists(source), "createdump.exe expected next to the .NET runtime: " + source);
        var exe = Path.Combine(TempFolder(), "stand-in.exe");
        File.Copy(source, exe);
        using var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileSha = Convert.ToHexStringLower(SHA256.HashData(hold));
        var leaf = Authenticode.Verify(hold.SafeFileHandle, exe, checkRevocation: false).LeafSha256!;
        var pin = new PawnIoPin(fileSha, leaf, "test");

        var ran = PawnIoRunner.Run(hold, exe, pin, ["--help"], TextWriter.Null, TimeSpan.FromMinutes(1));
        Assert.True(ran.Started, ran.Reason);
        Assert.NotNull(ran.Exit);

        Assert.False(PawnIoRunner.Run(hold, exe, pin with { FileSha256 = leaf }, ["--help"], TextWriter.Null, TimeSpan.FromMinutes(1)).Started);
        Assert.False(PawnIoRunner.Run(hold, exe, pin with { LeafCertSha256 = fileSha }, ["--help"], TextWriter.Null, TimeSpan.FromMinutes(1)).Started);
        Assert.False(PawnIoRunner.Run(hold, exe, new PawnIoPin("", "", ""), ["--help"], TextWriter.Null, TimeSpan.FromMinutes(1)).Started);
    }

    [Fact]
    public void Suspended_process_shows_its_image_and_is_terminated_before_it_runs()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var exe = Path.Combine(TempFolder(), "suspended-ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var process = GuardedProcess.StartSuspended(exe, ["-n", "30", "127.0.0.1"], Path.GetDirectoryName(exe)!);
        Assert.True(PawnIoPolicy.SameImage(Authenticode.ImagePath(process.Process), exe));
        Assert.Null(process.WaitForExit(TimeSpan.FromSeconds(2))); // suspended: does not end by itself
        process.Terminate(77);
        Assert.Equal(77, process.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.False(process.Resumed);
    }

    [Fact]
    public void Closing_the_job_kills_what_the_process_left_running()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var name = "t5-jobchild-" + Guid.NewGuid().ToString("N")[..8];
        var child = Path.Combine(TempFolder(), name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), child);
        Assert.DoesNotContain(" ", child, StringComparison.Ordinal); // "start" would take a quoted path as a window title
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using (var process = GuardedProcess.StartSuspended(cmd, ["/c", "start", "/b", child, "-n", "60", "127.0.0.1"], Path.GetDirectoryName(child)!))
        {
            process.Resume();
            Assert.NotNull(process.WaitForExit(TimeSpan.FromSeconds(30)));
            Assert.True(WaitFor(() => Process.GetProcessesByName(name).Length > 0), "the child should still run after cmd ended");
        }
        Assert.True(WaitFor(() => Process.GetProcessesByName(name).Length == 0), "closing the job must kill the child");
    }

    [Fact]
    public void Arguments_are_quoted_for_the_command_line()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Equal("-install", GuardedProcess.Quote("-install"));
        Assert.Equal("\"C:\\Program Files\\x.exe\"", GuardedProcess.Quote(@"C:\Program Files\x.exe"));
        Assert.Equal("\"\"", GuardedProcess.Quote(""));
        Assert.Equal("\"a\\\"b\"", GuardedProcess.Quote("a\"b"));
        Assert.Equal("\"a b\\\\\"", GuardedProcess.Quote(@"a b\"));
    }

    private static bool WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    // ------------------------------------------------------------ part B: transaction on NTFS

    [SupportedOSPlatform("windows")]
    private string ProtectedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-t5b-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, DataDirectoryFileSystem.TryCreateProtectedDirectory(root, withOwner: true));
        _cleanup.Add(root);
        using var open = new DataDirectoryFileSystem().Open(root, DataAccess.Inspect);
        return open.State.FinalPath ?? root; // long path: staging final-path checks compare against it
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Components_are_replaced_by_atomic_renames_and_rolled_back_on_ntfs()
    {
        if (!Elevated() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = ProtectedRoot();
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Join(root, ".staging-" + id);
        Directory.CreateDirectory(Path.Join(root, "Agent"));
        File.WriteAllText(Path.Join(root, "Agent", "v.txt"), "old");
        Directory.CreateDirectory(Path.Join(staging, "Agent"));
        File.WriteAllText(Path.Join(staging, "Agent", "v.txt"), "new");
        var fs = new WindowsInstallFileSystem();

        var services = new FakeServices();
        services.Health.Enqueue("not listening");
        var failed = new InstallTransaction(fs, services, root, TextWriter.Null).Run(staging, ["Agent"], () => { });
        Assert.Equal(TransactionResult.RolledBack, failed.Result);
        Assert.Equal("old", File.ReadAllText(Path.Join(root, "Agent", "v.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Join(staging, "Agent", "v.txt")));
        Assert.False(Directory.Exists(Path.Join(root, "Agent.old-" + id)));

        var tx = new InstallTransaction(fs, new FakeServices(), root, TextWriter.Null);
        Assert.Equal(TransactionResult.Committed, tx.Run(staging, ["Agent"], () => { }).Result);
        tx.Finish(staging);
        Assert.Equal("new", File.ReadAllText(Path.Join(root, "Agent", "v.txt")));
        Assert.False(Directory.Exists(Path.Join(root, "Agent.old-" + id)));
        Assert.False(Directory.Exists(staging));
        Assert.False(File.Exists(InstallJournal.PathIn(root)));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Rename_never_replaces_an_existing_folder()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = TempFolder();
        Directory.CreateDirectory(Path.Join(root, "a"));
        Directory.CreateDirectory(Path.Join(root, "b"));
        Assert.Throws<IOException>(() => AtomicMove.Rename(Path.Join(root, "a"), Path.Join(root, "b")));
        AtomicMove.Rename(Path.Join(root, "a"), Path.Join(root, "c"));
        Assert.True(Directory.Exists(Path.Join(root, "c")));
        Assert.False(Directory.Exists(Path.Join(root, "a")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void Junction_in_place_of_an_old_folder_is_refused_and_its_target_untouched(bool committed)
    {
        if (!Elevated() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var root = ProtectedRoot();
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Join(root, ".staging-" + id);
        Assert.Equal(0, DataDirectoryFileSystem.TryCreateProtectedDirectory(staging, withOwner: true));
        var target = TempFolder();
        File.WriteAllText(Path.Join(target, "precious.txt"), "keep");
        Directory.CreateDirectory(Path.Join(root, "Agent"));
        var old = InstallJournal.OldPath(root, "Agent", id);
        RunCmd("/c", "mklink", "/J", old, target);
        var fs = new WindowsInstallFileSystem();
        fs.WriteJournal(InstallJournal.PathIn(root), new InstallJournal(id, staging, false,
            committed ? InstallJournal.Committed : InstallJournal.Replacing, [new JournalComponent("Agent", true)]).Serialize());
        Assert.Throws<UnsafeInstallObjectException>(() => fs.CheckObject(old));

        var result = new InstallTransaction(fs, new FakeServices(), root, TextWriter.Null).Recover();
        Assert.Equal(committed ? RecoveryResult.Cleaned : RecoveryResult.Failed, result);
        Assert.Equal("keep", File.ReadAllText(Path.Join(target, "precious.txt")));
        Assert.True(Directory.Exists(old)); // the junction itself is left as it was
        Assert.True(Directory.Exists(Path.Join(root, "Agent"))); // nothing was moved before the refusal
        Directory.Delete(old); // removes the junction only
        Assert.Equal("keep", File.ReadAllText(Path.Join(target, "precious.txt")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Journal_survives_an_interrupted_write()
    {
        if (!Windows() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var path = InstallJournal.PathIn(TempFolder());
        var fs = new WindowsInstallFileSystem();
        fs.WriteJournal(path, "first");
        File.WriteAllText(path + ".tmp", "half writ"); // a write cut off before its rename
        Assert.Equal("first", fs.ReadJournal(path));
        fs.WriteJournal(path, "second");
        Assert.Equal("second", fs.ReadJournal(path));
        Assert.False(File.Exists(path + ".tmp"));
        fs.DeleteJournal(path);
        Assert.Null(fs.ReadJournal(path));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Installed_folders_let_users_read_and_run_but_not_write()
    {
        if (!Elevated() || !OperatingSystem.IsWindows())
        {
            return;
        }
        var dir = Path.Join(ProtectedRoot(), "Tray");
        Directory.CreateDirectory(Path.Join(dir, "sub"));
        File.WriteAllText(Path.Join(dir, "sub", "x.dll"), "x");
        new WindowsInstallFileSystem().ProtectInstalled(dir);
        var fs = new DataDirectoryFileSystem();
        foreach (var path in new[] { dir, Path.Join(dir, "sub"), Path.Join(dir, "sub", "x.dll") })
        {
            using var open = fs.Open(path, DataAccess.Inspect);
            var users = open.State.Dacl.Where(a => a.Sid == "S-1-5-32-545").ToList();
            Assert.NotEmpty(users);
            Assert.All(users, a => Assert.Equal(0u, a.Mask & StagingPathPolicy.WriteRightsOnOwnFolders));
            Assert.All(users, a => Assert.Equal(0x1200A9u, a.Mask & 0x1200A9u));
            Assert.All(open.State.Dacl, a => Assert.Contains(a.Sid, new[] { DataDirectoryPolicy.LocalSystem, DataDirectoryPolicy.Administrators, "S-1-5-32-545" }));
            output.WriteLine($"{path}: {open.State.SecurityDescriptor}");
        }
        using (var top = fs.Open(dir, DataAccess.Inspect))
        {
            Assert.True(top.State.DaclProtected);
            Assert.Equal(DataDirectoryPolicy.Administrators, top.State.OwnerSid);
        }
    }

    private static void RunCmd(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, text);
    }

    public void Dispose()
    {
        foreach (var path in _cleanup.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException e)
            {
                output.WriteLine("cleanup: " + e.Message);
            }
        }
    }
}
