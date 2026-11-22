using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Storage;
using Nexus.Setup;
using Xunit;

namespace Nexus.Tests;

public sealed class PayloadManifestTests
{
    private static readonly byte[] Agent = Encoding.UTF8.GetBytes("agent");
    private static readonly byte[] Tray = Encoding.UTF8.GetBytes("tray!");

    private static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static List<ManifestEntry> Manifest() =>
        [new("Agent/Nexus.Agent.exe", Agent.Length, Hash(Agent)), new("Tray/Nexus.Tray.exe", Tray.Length, Hash(Tray))];

    private static List<(string, long)> Archive() =>
        [("manifest.json", 10), ("Agent/", 0), ("Agent/Nexus.Agent.exe", Agent.Length), ("Tray/Nexus.Tray.exe", Tray.Length)];

    [Fact]
    public void Matching_archive_and_written_files_pass()
    {
        Assert.Null(PayloadManifest.CheckArchive(Archive(), Manifest()));
        Assert.Null(PayloadManifest.CheckWritten(Manifest()[0], new MemoryStream(Agent)));
    }

    [Fact]
    public void Wrong_hash_of_written_file_is_refused()
    {
        Assert.NotNull(PayloadManifest.CheckWritten(Manifest()[0], new MemoryStream(Encoding.UTF8.GetBytes("agenT"))));
        Assert.NotNull(PayloadManifest.CheckWritten(Manifest()[0], new MemoryStream(Encoding.UTF8.GetBytes("agent+"))));
    }

    [Fact]
    public void Extra_file_is_refused() =>
        Assert.StartsWith("not in manifest", PayloadManifest.CheckArchive([.. Archive(), ("Agent/evil.dll", 3)], Manifest()));

    [Fact]
    public void Missing_file_is_refused() =>
        Assert.StartsWith("missing", PayloadManifest.CheckArchive([.. Archive().Where(e => e.Item1 != "Tray/Nexus.Tray.exe")], Manifest()));

    [Fact]
    public void Missing_manifest_is_refused() =>
        Assert.Equal("no manifest", PayloadManifest.CheckArchive([.. Archive().Where(e => e.Item1 != "manifest.json")], Manifest()));

    [Fact]
    public void Size_mismatch_in_archive_is_refused() =>
        Assert.StartsWith("size differs", PayloadManifest.CheckArchive([("manifest.json", 1), ("Agent/Nexus.Agent.exe", 99), ("Tray/Nexus.Tray.exe", Tray.Length)], Manifest()));

    [Theory]
    [InlineData("Agent/../evil.exe")]
    [InlineData("../evil.exe")]
    [InlineData("/Agent/x.exe")]
    [InlineData("C:/x.exe")]
    [InlineData("Agent/x.exe:stream")]
    [InlineData("Agent/x.exe.")]
    [InlineData("Agent//x.exe")]
    public void Unsafe_paths_are_refused(string path)
    {
        Assert.StartsWith("unsafe path", PayloadManifest.CheckArchive([.. Archive(), (path, 1)], Manifest()));
        Assert.False(PayloadManifest.IsSafeRelativePath(path));
    }

    [Fact]
    public void Names_differing_only_in_case_are_refused() =>
        Assert.StartsWith("duplicate", PayloadManifest.CheckArchive([.. Archive(), ("agent/nexus.agent.exe", Agent.Length)], Manifest()));

    [Fact]
    public void Manifest_json_is_parsed_strictly()
    {
        var good = $$"""{"version":1,"files":[{"path":"Agent/Nexus.Agent.exe","size":5,"sha256":"{{Hash(Agent)}}"}]}""";
        Assert.Equal(Manifest()[0], Assert.Single(PayloadManifest.Parse(good)!));
        Assert.Null(PayloadManifest.Parse(good.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)));
        Assert.Null(PayloadManifest.Parse(good.Replace(Hash(Agent), Hash(Agent).ToUpperInvariant(), StringComparison.Ordinal)));
        Assert.Null(PayloadManifest.Parse(good.Replace("Agent/Nexus", "../Nexus", StringComparison.Ordinal)));
        Assert.Null(PayloadManifest.Parse(good.Replace("\"size\":5", "\"size\":-1", StringComparison.Ordinal)));
        var dup = $$"""{"version":1,"files":[{"path":"A/x","size":1,"sha256":"{{Hash(Agent)}}"},{"path":"a/X","size":1,"sha256":"{{Hash(Agent)}}"}]}""";
        Assert.Null(PayloadManifest.Parse(dup));
        Assert.Null(PayloadManifest.Parse("not json"));
        Assert.Null(PayloadManifest.Parse("""{"version":1,"files":[]}"""));
    }
}

public sealed class StagingPathPolicyTests
{
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";

    private static readonly DataAce SystemFull = new(DataDirectoryPolicy.LocalSystem, true, 0x1F01FF, 0);
    private static readonly DataAce AdminsFull = new(DataDirectoryPolicy.Administrators, true, 0x1F01FF, 0);
    private static readonly DataAce UsersRead = new(Users, true, 0x1200A9, 0);

    private static List<PathComponentState> Standard() =>
    [
        new(@"C:\", false, DataDirectoryPolicy.TrustedInstaller,
            [SystemFull, AdminsFull, UsersRead, new(AuthenticatedUsers, true, 0x4, 0), new(AuthenticatedUsers, true, 0x10000, 0x08 | 0x01 | 0x02), new(CreatorOwner, true, 0x10000000, 0x08 | 0x03)],
            IsVolumeRoot: true),
        new(@"C:\Program Files", false, DataDirectoryPolicy.TrustedInstaller, [SystemFull, AdminsFull, UsersRead]),
        new(@"C:\Program Files\NexusPC", false, DataDirectoryPolicy.Administrators, [SystemFull, AdminsFull, UsersRead]),
        new(@"C:\Program Files\NexusPC\.staging-0", false, DataDirectoryPolicy.Administrators, [SystemFull, AdminsFull]),
    ];

    [Fact]
    public void Standard_path_with_create_folders_on_the_root_is_accepted() => Assert.Null(StagingPathPolicy.Check(Standard()));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Reparse_point_at_any_level_is_refused(int level)
    {
        var path = Standard();
        path[level] = path[level] with { IsReparsePoint = true };
        Assert.StartsWith("reparse point", StagingPathPolicy.Check(path));
    }

    [Theory]
    [InlineData(StagingPathPolicy.Delete)]
    [InlineData(StagingPathPolicy.WriteDac)]
    [InlineData(StagingPathPolicy.WriteOwner)]
    [InlineData(StagingPathPolicy.GenericAll)]
    public void Untrusted_ace_that_can_replace_a_component_is_refused(uint mask)
    {
        for (var level = 0; level < 4; level++)
        {
            var path = Standard();
            path[level] = path[level] with { Dacl = [.. path[level].Dacl, new DataAce(Users, true, mask, 0)] };
            Assert.Contains("may replace", StagingPathPolicy.Check(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Delete_child_on_a_parent_is_refused()
    {
        for (var level = 0; level < 3; level++)
        {
            var path = Standard();
            path[level] = path[level] with { Dacl = [.. path[level].Dacl, new DataAce(AuthenticatedUsers, true, StagingPathPolicy.FileDeleteChild, 0)] };
            Assert.Contains("may delete or rename", StagingPathPolicy.Check(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Untrusted_owner_is_refused()
    {
        var path = Standard();
        path[2] = path[2] with { OwnerSid = "S-1-5-21-1-2-3-1001" };
        Assert.StartsWith("untrusted owner", StagingPathPolicy.Check(path));
    }

    [Fact]
    public void Deny_and_inherit_only_aces_do_not_count()
    {
        var path = Standard();
        path[1] = path[1] with { Dacl = [.. path[1].Dacl, new DataAce(Users, false, StagingPathPolicy.Delete, 0), new DataAce(Users, true, StagingPathPolicy.Delete, 0x08)] };
        Assert.Null(StagingPathPolicy.Check(path));
    }
}

public sealed class StagingPathShapeTests
{
    private const string Staging = @"C:\Program Files\NexusPC\.staging-0123";

    [Fact]
    public void Components_go_from_the_volume_root_down() =>
        Assert.Equal([@"C:\", @"C:\Program Files", @"C:\Program Files\NexusPC", Staging], StagingPathPolicy.Components(Staging));

    [Theory]
    [InlineData(@"\\server\share\NexusPC")]
    [InlineData(@"\\?\C:\Program Files\NexusPC")]
    [InlineData(@"Program Files\NexusPC")]
    [InlineData(@"C:\Program Files\..\Users\NexusPC")]
    [InlineData(@"C:\Program Files\.\NexusPC")]
    [InlineData(@"C:\Program Files\\NexusPC")]
    [InlineData(@"C:\Program Files\NexusPC:ads")]
    public void Unusual_paths_have_no_components_and_are_refused(string path) => Assert.Empty(StagingPathPolicy.Components(path));

    private static PathComponentState Created(string? owner = DataDirectoryPolicy.Administrators, bool reparse = false, params DataAce[] dacl) =>
        new(Staging, reparse, owner, dacl.Length > 0 ? dacl
            : [new(DataDirectoryPolicy.LocalSystem, true, 0x1F01FF, 3), new(DataDirectoryPolicy.Administrators, true, 0x1F01FF, 3)]);

    [Fact]
    public void Created_staging_must_be_administrators_owned_protected_and_at_the_expected_path()
    {
        Assert.Null(StagingPathPolicy.CheckCreatedStaging(Staging, Created(), true, Staging.ToUpperInvariant()));
        Assert.NotNull(StagingPathPolicy.CheckCreatedStaging(Staging, Created(), false, Staging));
        Assert.NotNull(StagingPathPolicy.CheckCreatedStaging(Staging, Created(DataDirectoryPolicy.LocalSystem), true, Staging));
        Assert.NotNull(StagingPathPolicy.CheckCreatedStaging(Staging, Created(reparse: true), true, Staging));
        Assert.NotNull(StagingPathPolicy.CheckCreatedStaging(Staging, Created(dacl: new DataAce("S-1-5-11", true, 0x1200A9, 0)), true, Staging));
        Assert.NotNull(StagingPathPolicy.CheckCreatedStaging(Staging, Created(), true, @"D:\elsewhere\.staging-0123"));
        Assert.NotNull(StagingPathPolicy.CheckCreatedStaging(Staging, Created(), true, null));
    }

    [Fact]
    public void Unrecognised_allow_ace_on_the_path_is_refused()
    {
        var path = new List<PathComponentState>
        {
            new(@"C:\", false, DataDirectoryPolicy.TrustedInstaller, [new(DataDirectoryPolicy.Administrators, true, 0x1F01FF, 0), new("(ACE type 9)", true, 0, 0)]),
        };
        Assert.StartsWith("unrecognised", StagingPathPolicy.Check(path));
    }

    [Fact]
    public void Free_space_needed_is_twice_the_payload_plus_a_margin() =>
        Assert.Equal((2L * 300) + (64L * 1024 * 1024), PayloadManifest.RequiredFreeBytes([new("a", 100, new string('0', 64)), new("b", 200, new string('0', 64))]));

    [Fact]
    public void Payload_with_its_manifest_passes_the_name_check()
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "Agent/Nexus.Agent.exe", "Tray/Nexus.Tray.exe", "manifest.json", "version.txt", SetupPlan.PawnIoSetup })
            {
                zip.CreateEntry(name);
            }
        }
        memory.Position = 0;
        using var read = new System.IO.Compression.ZipArchive(memory);
        Assert.Null(SetupPlan.CheckPayload(read));
    }
}

public sealed class PawnIoPolicyTests
{
    private const string FileHash = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
    private const string Leaf = "8166cb6a0b4d058ecf5e499193e3ee74f1da7b3aaa6fde89ca2111e61f0af792";
    private static readonly PawnIoPin Pin = new(FileHash, Leaf, "2.2.0");

    private static Func<bool, (int, string?)> Verify(int strict, int relaxed = 0, string? leaf = Leaf, List<bool>? calls = null) => revocation =>
    {
        calls?.Add(revocation);
        var code = revocation ? strict : relaxed;
        return (code, code == 0 ? leaf : null);
    };

    [Fact]
    public void Pinned_file_with_valid_signature_and_exact_leaf_is_allowed()
    {
        var verdict = PawnIoPolicy.Check(Pin, FileHash.ToUpperInvariant(), Verify(0));
        Assert.True(verdict.Allowed);
        Assert.False(verdict.RevocationUnknown);
    }

    [Fact]
    public void Missing_pins_refuse_without_checking()
    {
        var calls = new List<bool>();
        Assert.False(PawnIoPolicy.Check(new PawnIoPin("", "", ""), FileHash, Verify(0, calls: calls)).Allowed);
        Assert.False(PawnIoPolicy.Check(Pin with { LeafCertSha256 = "abc" }, FileHash, Verify(0, calls: calls)).Allowed);
        Assert.Empty(calls);
    }

    [Fact]
    public void Wrong_file_hash_is_refused_before_the_signature_is_looked_at()
    {
        var calls = new List<bool>();
        Assert.False(PawnIoPolicy.Check(Pin, Leaf, Verify(0, calls: calls)).Allowed);
        Assert.Empty(calls);
    }

    [Fact]
    public void Other_leaf_certificate_is_refused_even_with_a_valid_signature() =>
        Assert.False(PawnIoPolicy.Check(Pin, FileHash, Verify(0, leaf: FileHash)).Allowed);

    [Fact]
    public void Revoked_or_invalid_signature_is_refused_and_not_retried()
    {
        var calls = new List<bool>();
        Assert.False(PawnIoPolicy.Check(Pin, FileHash, Verify(PawnIoPolicy.CertERevoked, calls: calls)).Allowed);
        Assert.False(PawnIoPolicy.Check(Pin, FileHash, Verify(unchecked((int)0x800B0100), calls: calls)).Allowed);
        Assert.Equal([true, true], calls);
    }

    [Theory]
    [InlineData(PawnIoPolicy.CryptERevocationOffline)]
    [InlineData(PawnIoPolicy.CryptENoRevocationCheck)]
    [InlineData(PawnIoPolicy.CertERevocationFailure)]
    public void Unknown_revocation_is_allowed_only_with_valid_signature_and_exact_leaf_and_is_flagged(int offline)
    {
        var allowed = PawnIoPolicy.Check(Pin, FileHash, Verify(offline));
        Assert.True(allowed.Allowed);
        Assert.True(allowed.RevocationUnknown);
        Assert.False(PawnIoPolicy.Check(Pin, FileHash, Verify(offline, relaxed: unchecked((int)0x800B0109))).Allowed);
        Assert.False(PawnIoPolicy.Check(Pin, FileHash, Verify(offline, leaf: FileHash)).Allowed);
    }

    [Fact]
    public void Started_image_must_be_the_staged_file()
    {
        const string staged = @"C:\Program Files\NexusPC\.staging-1\PawnIO\PawnIO_setup.exe";
        Assert.True(PawnIoPolicy.SameImage(staged.ToUpperInvariant(), staged));
        Assert.True(PawnIoPolicy.SameImage(@"\\?\" + staged, staged));
        Assert.False(PawnIoPolicy.SameImage(@"C:\Users\x\AppData\Local\Temp\PawnIO_setup.exe", staged));
        Assert.False(PawnIoPolicy.SameImage(null, staged));
    }

    [Fact]
    public void Developer_build_has_no_pins() => Assert.False(PawnIoPin.FromAssembly().Complete);
}

public sealed class PayloadBackslashNameTests
{
    private static readonly List<ManifestEntry> Manifest = [new("Agent/Nexus.Agent.exe", 5, new string('a', 64)), new("Tray/Nexus.Tray.exe", 5, new string('b', 64))];

    [Fact]
    public void Archive_written_with_backslashes_matches_the_manifest() =>
        Assert.Null(PayloadManifest.CheckArchive([("manifest.json", 9), ("Agent\\", 0), ("Agent\\Nexus.Agent.exe", 5), ("Tray\\Nexus.Tray.exe", 5)], Manifest));

    [Fact]
    public void Backslash_and_slash_spellings_of_one_name_are_duplicates() =>
        Assert.StartsWith("duplicate", PayloadManifest.CheckArchive([("manifest.json", 9), ("Agent\\Nexus.Agent.exe", 5), ("Agent/Nexus.Agent.exe", 5), ("Tray/Nexus.Tray.exe", 5)], Manifest));

    [Theory]
    [InlineData("..\\x.exe")]
    [InlineData("Agent\\..\\x.exe")]
    [InlineData("\\Agent\\x.exe")]
    [InlineData("C:\\x.exe")]
    [InlineData("Agent\\\\x.exe")]
    public void Unsafe_backslash_paths_are_refused(string name) =>
        Assert.StartsWith("unsafe path", PayloadManifest.CheckArchive([("manifest.json", 9), (name, 1), ("Agent/Nexus.Agent.exe", 5), ("Tray/Nexus.Tray.exe", 5)], Manifest));

    [Fact]
    public void Backslash_in_manifest_json_is_refused()
    {
        Assert.False(PayloadManifest.IsSafeRelativePath("Agent\\x.exe"));
        Assert.Null(PayloadManifest.Parse($$"""{"version":1,"files":[{"path":"Agent\\Nexus.Agent.exe","size":5,"sha256":"{{new string('a', 64)}}"}]}"""));
    }

    [Fact]
    public void Extraction_finds_a_backslash_entry_by_its_normalized_name()
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("Agent\\Nexus.Agent.exe");
        }
        memory.Position = 0;
        using var read = new System.IO.Compression.ZipArchive(memory);
        Assert.NotNull(PayloadManifest.FindEntry(read, "Agent/Nexus.Agent.exe"));
        Assert.Null(PayloadManifest.FindEntry(read, "agent/nexus.agent.exe"));
        Assert.Null(PayloadManifest.FindEntry(read, "Tray/Nexus.Tray.exe"));
    }
}

public sealed class SecurityRoundTests
{
    private static readonly DataAce SystemFull = new(DataDirectoryPolicy.LocalSystem, true, 0x1F01FF, 0);
    private static readonly DataAce AdminsFull = new(DataDirectoryPolicy.Administrators, true, 0x1F01FF, 0);
    private const string Users = "S-1-5-32-545";
    private const int OiCiIo = 0x01 | 0x02 | 0x08;

    private static List<PathComponentState> Path(params DataAce[] onNexusPc) =>
    [
        new(@"C:\", false, DataDirectoryPolicy.TrustedInstaller, [SystemFull, AdminsFull, new("S-1-5-11", true, 0x4, 0)], IsVolumeRoot: true),
        new(@"C:\Program Files", false, DataDirectoryPolicy.TrustedInstaller,
            [SystemFull, AdminsFull, new(Users, true, 0x1200A9, 0), new(Users, true, 0xA0000000, OiCiIo), new("S-1-3-0", true, 0x10000000, OiCiIo)]),
        new(@"C:\Program Files\NexusPC", false, DataDirectoryPolicy.Administrators,
            [SystemFull, AdminsFull, new(Users, true, 0x1200A9, 0x10), new(Users, true, 0xA0000000, OiCiIo | 0x10), new("S-1-3-0", true, 0x10000000, OiCiIo | 0x10), .. onNexusPc]),
    ];

    [Fact]
    public void Default_inherited_nexuspc_acl_is_accepted_in_strict_mode() => Assert.Null(StagingPathPolicy.Check(Path(), strictFromLevel: 2));

    [Fact]
    public void Inherit_only_modify_for_users_on_nexuspc_is_refused()
    {
        var path = Path(new DataAce(Users, true, 0x1301BF, OiCiIo));
        Assert.Contains("may write into", StagingPathPolicy.Check(path, strictFromLevel: 2), StringComparison.Ordinal);
        Assert.Null(StagingPathPolicy.Check(path)); // the same ACE is not looked at above our own folders
    }

    [Theory]
    [InlineData(StagingPathPolicy.FileAddFile)]
    [InlineData(StagingPathPolicy.FileAddSubdirectory)]
    [InlineData(StagingPathPolicy.FileWriteEa)]
    [InlineData(StagingPathPolicy.FileWriteAttributes)]
    [InlineData(StagingPathPolicy.MaximumAllowed)]
    [InlineData(StagingPathPolicy.GenericWrite)]
    public void Any_write_right_for_users_on_nexuspc_is_refused(uint mask) =>
        Assert.Contains("may write into", StagingPathPolicy.Check(Path(new DataAce(Users, true, mask, 0)), strictFromLevel: 2), StringComparison.Ordinal);

    [Fact]
    public void Creator_owner_is_exempt_only_when_inherit_only() =>
        Assert.NotNull(StagingPathPolicy.Check(Path(new DataAce("S-1-3-0", true, 0x2, 0)), strictFromLevel: 2));

    [Theory]
    [InlineData("CON")]
    [InlineData("Agent/nul")]
    [InlineData("Agent/com1.dll")]
    [InlineData("LPT9.txt.bak")]
    [InlineData("Agent/Aux /x")]
    [InlineData("Agent/CONIN$")]
    [InlineData("Agent/COM\u00B9.txt")]
    public void Windows_device_names_are_refused(string path) => Assert.False(PayloadManifest.IsSafeRelativePath(path));

    [Theory]
    [InlineData("Agent/console.dll")]
    [InlineData("Agent/COM10.dll")]
    [InlineData("Agent/nullable.json")]
    [InlineData("LPT.txt")]
    public void Names_that_only_resemble_devices_are_allowed(string path) => Assert.True(PayloadManifest.IsSafeRelativePath(path));

    private const string Good = """{"version":1,"files":[{"path":"a","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";

    [Fact]
    public void Manifest_is_read_up_to_four_megabytes_only()
    {
        Assert.NotNull(PayloadManifest.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Good))));
        var padded = Good + new string(' ', PayloadManifest.MaxManifestBytes - Good.Length);
        Assert.NotNull(PayloadManifest.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(padded))));
        var reader = new CountingStream(System.Text.Encoding.UTF8.GetBytes(padded + " "));
        Assert.Null(PayloadManifest.Read(reader));
        Assert.True(reader.Delivered <= PayloadManifest.MaxManifestBytes + 1);
        Assert.Null(PayloadManifest.Read(new MemoryStream()));
        Assert.Null(PayloadManifest.Read(new MemoryStream([0x7B, 0xFF, 0x7D])));
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public long Delivered { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            Delivered += read;
            return read;
        }
    }
}
