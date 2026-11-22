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
    [InlineData("Agent\\x.exe")]
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
