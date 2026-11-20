using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Agent;
using Nexus.Core.Storage;
using static Nexus.Tests.TestPaths;

namespace Nexus.Tests;

/// <summary>Thrown by the fake file system to simulate the process dying in the middle of a step.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException() : base("simulated crash")
    {
    }
}

/// <summary>
/// In-memory file system for the data folder logic: identities, links, owners, ACEs, contents and
/// fault injection (no space, failing copy, corrupt copy, crash after k changes, objects swapped).
/// </summary>
internal sealed class FakeDataFileSystem : IDataFileSystem
{
    public const string Users = "S-1-5-32-545";
    public const string SomeUser = "S-1-5-21-1-2-3-1001";
    private int _nextId = 1;

    public sealed class Node
    {
        public required DataObjectKind Kind { get; set; }
        public required string Id { get; set; }
        public uint Links { get; set; } = 1;
        public string? FinalOverride { get; set; }
        public string Owner { get; set; } = DataDirectoryPolicy.Administrators;
        public bool Protected { get; set; }
        public List<DataAce> Aces { get; set; } = [];
        public byte[] Content { get; set; } = [];
        /// <summary>A real SDDL text reported instead of the JSON form until the repair rewrites the access.</summary>
        public string? RawSddl { get; set; }
    }

    public Dictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);
    public long Available { get; set; } = long.MaxValue / 4;
    public int? FailCreateFileAt { get; set; }
    public bool CorruptCopies { get; set; }
    public int? CrashAfterSecurityChanges { get; set; }
    /// <summary>Crash right after the (k+1)-th change is applied, before the journal records it.</summary>
    public int? CrashAfterChangeBeforeJournal { get; set; }
    public bool CrashAfterRenameBeforeJournal { get; set; }
    /// <summary>Durable writes for which this returns true fail with an IOException.</summary>
    public Func<string, bool>? FailDurableWrite { get; set; }
    public string? ExtraNamesIn { get; set; }
    public IReadOnlyList<string> ExtraNames { get; set; } = [];
    public bool FailMoveDirectory { get; set; }
    public Action<string, DataAccess>? OnOpen { get; set; }
    /// <summary>What ReadSecurityDescriptor returns, given the path and the descriptor the fake holds (null: unreadable).</summary>
    public Func<string, string, string?>? ReadBack { get; set; }
    public int SecurityChanges { get; private set; }
    public int Renames { get; private set; }
    private int _createdFiles;

    public static List<DataAce> TrustedAces() =>
    [
        new(DataDirectoryPolicy.LocalSystem, true, DataDirectoryPolicy.FullControl, 0x3),
        new(DataDirectoryPolicy.Administrators, true, DataDirectoryPolicy.FullControl, 0x3),
    ];

    public Node AddDirectory(string path, string owner = DataDirectoryPolicy.Administrators, List<DataAce>? aces = null)
    {
        var node = new Node { Kind = DataObjectKind.Directory, Id = NewId(), Owner = owner, Aces = aces ?? TrustedAces() };
        Nodes[path] = node;
        return node;
    }

    public Node AddFile(string path, string content, string owner = DataDirectoryPolicy.Administrators, List<DataAce>? aces = null)
    {
        var node = new Node { Kind = DataObjectKind.File, Id = NewId(), Owner = owner, Aces = aces ?? TrustedAces(), Content = Encoding.UTF8.GetBytes(content) };
        Nodes[path] = node;
        return node;
    }

    public string NewId() => (_nextId++).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Everything at or below <paramref name="root"/>: identity, links, owner, ACEs and a content hash.</summary>
    public string Snapshot(string root) => string.Join("\n", Nodes
        .Where(n => n.Key == root || n.Key.StartsWith(root + TestPaths.Sep, StringComparison.Ordinal))
        .OrderBy(n => n.Key, StringComparer.Ordinal)
        .Select(n => $"{n.Key}|{n.Value.Kind}|{n.Value.Id}|{n.Value.Links}|{Sddl(n.Value)}|{Convert.ToHexString(SHA256.HashData(n.Value.Content))}"));

    public static string Sddl(Node node) => node.RawSddl ?? JsonSerializer.Serialize(new SddlForm(node.Owner, node.Protected, node.Aces));

    private sealed record SddlForm(string Owner, bool Protected, List<DataAce> Aces);

    public IDataObject Open(string fullPath, DataAccess access)
    {
        OnOpen?.Invoke(fullPath, access);
        if (!Nodes.TryGetValue(fullPath, out var node))
        {
            throw new DataAccessException("not found: " + fullPath, null, notFound: true);
        }
        return new FakeObject(this, fullPath, node);
    }

    private static DataObjectState State(string path, Node node) => new()
    {
        FullPath = path,
        Kind = node.Kind,
        LinkCount = node.Links,
        FinalPath = node.FinalOverride ?? path,
        Id = new DataObjectId(7, node.Id),
        Size = node.Kind == DataObjectKind.File ? node.Content.Length : 0,
        LastWriteUtc = Shared.Now,
        OwnerSid = node.Owner,
        DaclProtected = node.Protected,
        Dacl = [.. node.Aces],
        SecurityDescriptor = Sddl(node),
    };

    public IReadOnlyList<string> ListChildren(IDataObject directory)
    {
        var path = directory.State.FullPath;
        return [.. Nodes.Keys.Where(k => TestPaths.Parent(k) == path).Select(TestPaths.Name),.. path == ExtraNamesIn ? ExtraNames : []];
    }

    /// <summary>Other names of the same folder (8.3 names, "All Users", \\?\ forms) and what they resolve to.</summary>
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ExpectedRootFinalPath(string root) => Aliases.TryGetValue(root, out var real) ? real : root;

    public long AvailableBytes(string directory) => Available;

    public bool DirectoryExists(string path) => Nodes.TryGetValue(path, out var n) && n.Kind == DataObjectKind.Directory;

    public IReadOnlyList<string> Siblings(string path, string suffixPrefix) =>
        [.. Nodes.Keys.Where(k => TestPaths.Parent(k) == TestPaths.Parent(path) && TestPaths.Name(k).StartsWith(TestPaths.Name(path) + suffixPrefix, StringComparison.Ordinal))];

    public void CreateProtectedDirectory(string path)
    {
        if (Nodes.ContainsKey(path))
        {
            throw new IOException("exists");
        }
        var node = AddDirectory(path);
        node.Protected = true;
    }

    public void CreateDirectory(string path)
    {
        if (Nodes.ContainsKey(path))
        {
            throw new IOException("exists");
        }
        AddDirectory(path);
    }

    public Stream CreateFile(string path)
    {
        if (Nodes.ContainsKey(path))
        {
            throw new IOException("exists");
        }
        _createdFiles++;
        if (_createdFiles == FailCreateFileAt)
        {
            throw new IOException("There is not enough space on the disk.");
        }
        var node = AddFile(path, "");
        return new CaptureStream(node.Id, bytes => node.Content = bytes);
    }

    public void FlushToDisk(Stream stream) => stream.Flush();

    public DataObjectId IdOf(Stream created) => new(7, ((CaptureStream)created).Id);

    public void WriteAllTextDurably(string path, string contents)
    {
        if (FailDurableWrite?.Invoke(path) == true)
        {
            throw new IOException("The disk is full.");
        }
        if (Nodes.TryGetValue(path, out var existing))
        {
            existing.Content = Encoding.UTF8.GetBytes(contents);
        }
        else
        {
            AddFile(path, contents);
        }
    }

    public string? ReadAllText(string path) => Nodes.TryGetValue(path, out var n) && n.Kind == DataObjectKind.File ? Encoding.UTF8.GetString(n.Content) : null;

    public IReadOnlyList<string> ListTree(string path) =>
        [.. Nodes.Keys.Where(k => k.StartsWith(path + TestPaths.Sep, StringComparison.Ordinal)).Select(k => k[(path.Length + 1)..])];

    public void MoveDirectory(string source, string destination)
    {
        if (FailMoveDirectory)
        {
            throw new IOException("The process cannot access the file because it is being used by another process.");
        }
        foreach (var key in Nodes.Keys.Where(k => k == source || k.StartsWith(source + TestPaths.Sep, StringComparison.Ordinal)).ToList())
        {
            Nodes[destination + key[source.Length..]] = Nodes[key];
            Nodes.Remove(key);
        }
    }

    private sealed class CaptureStream(string id, Action<byte[]> done) : MemoryStream
    {
        public string Id => id;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                done(ToArray());
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FakeObject(FakeDataFileSystem fs, string path, Node node) : IDataObject
    {
        public DataObjectState State { get; } = FakeDataFileSystem.State(path, node);

        public Stream OpenContent()
        {
            var bytes = node.Content;
            if (fs.CorruptCopies && path.Contains(DataDirectoryRepair.BackupInfix, StringComparison.Ordinal) && bytes.Length > 0)
            {
                bytes = [.. bytes];
                bytes[0] ^= 0xFF; // a backup copy that reads back differently
            }
            return new MemoryStream(bytes, writable: false);
        }

        public void SetSecurity(DataSecurity security)
        {
            if (fs.CrashAfterSecurityChanges is { } limit && fs.SecurityChanges >= limit)
            {
                throw new SimulatedCrashException();
            }
            fs.SecurityChanges++;
            if (security is not DataSecurity.Original)
            {
                node.RawSddl = null;
            }
            switch (security)
            {
                case DataSecurity.ProtectedRoot:
                    node.Owner = DataDirectoryPolicy.Administrators;
                    node.Protected = true;
                    node.Aces = TrustedAces();
                    break;
                case DataSecurity.InheritedFromRoot:
                    node.Owner = DataDirectoryPolicy.Administrators;
                    node.Protected = false;
                    node.Aces = [.. TrustedAces().Select(a => a with { Flags = 0x10 })];
                    break;
                case DataSecurity.Original original when original.Sddl.StartsWith("O:", StringComparison.Ordinal):
                    node.RawSddl = original.Sddl; // a real SDDL text given by a test: kept as is
                    break;
                case DataSecurity.Original original:
                    node.RawSddl = null;
                    var form = JsonSerializer.Deserialize<SddlForm>(original.Sddl)!;
                    node.Owner = form.Owner;
                    node.Protected = form.Protected;
                    node.Aces = form.Aces;
                    break;
            }
            if (fs.CrashAfterChangeBeforeJournal is { } after && fs.SecurityChanges == after + 1)
            {
                throw new SimulatedCrashException();
            }
        }

        public string? ReadSecurityDescriptor() => fs.ReadBack is { } readBack ? readBack(path, FakeDataFileSystem.Sddl(node)) : FakeDataFileSystem.Sddl(node);

        public void Rename(string newFullPath)
        {
            fs.Renames++;
            fs.MoveDirectory(path, newFullPath);
            if (fs.CrashAfterRenameBeforeJournal)
            {
                throw new SimulatedCrashException();
            }
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class ScriptedConsole(params string[] answers) : IRepairConsole
{
    private readonly Queue<string> _answers = new(answers);

    public List<string> Lines { get; } = [];

    public string Text => string.Join("\n", Lines);

    public void WriteLine(string text) => Lines.Add(text);

    public string? ReadLine() => _answers.Count > 0 ? _answers.Dequeue() : null;
}

/// <summary>The test paths have the platform's form, the same one production builds with Path.Join.</summary>
public sealed class TestPathsTests
{
    [Fact]
    public void Test_paths_use_the_platform_separator_and_match_Path_Join()
    {
        var sep = Path.DirectorySeparatorChar;
        var other = sep == '/' ? '\\' : '/';
        Assert.Equal(sep.ToString(), Sep);
        Assert.DoesNotContain(other, TestPaths.Root);
        Assert.DoesNotContain(other, Pd);
        Assert.True(Path.IsPathFullyQualified(TestPaths.Root));
        Assert.Equal(TestPaths.Root, Path.GetFullPath(TestPaths.Root)); // already normalized
        Assert.Equal(Pd, Path.GetDirectoryName(TestPaths.Root));
        Assert.Equal(OperatingSystem.IsWindows() ? @"C:\pd\NexusPC" : "/pd/NexusPC", TestPaths.Root);
        Assert.Equal($"{sep}OpenRGB{sep}plugins", Rel("/OpenRGB/plugins"));

        var joined = Path.Join(TestPaths.Root, "OpenRGB", "plugins");
        Assert.Equal(TestPaths.Root + Rel("/OpenRGB/plugins"), joined);
        Assert.Equal(Path.Join(TestPaths.Root, "OpenRGB"), Parent(joined));
        Assert.Equal("plugins", Name(joined));
        Assert.Equal(TestPaths.Root, Parent(Path.Join(TestPaths.Root, "devices.json")));
    }
}

public sealed class DataDirectoryPolicyTests
{
    private static readonly string Root = TestPaths.Root;
    private static readonly string ObjectPath = Root + TestPaths.Rel("/a");
    private const string SameAsObjectPath = "\0same";

    private static DataObjectState State(DataObjectKind kind = DataObjectKind.File, uint links = 1, string? final = SameAsObjectPath, string? owner = DataDirectoryPolicy.LocalSystem,
        bool daclPresent = true, params DataAce[] aces) => new()
        {
            FullPath = ObjectPath,
            Kind = kind,
            LinkCount = links,
            FinalPath = final == SameAsObjectPath ? ObjectPath : final,
            OwnerSid = owner,
            DaclPresent = daclPresent,
            Dacl = aces.Length == 0 ? FakeDataFileSystem.TrustedAces() : aces,
        };

    [Fact]
    public void Trusted_object_has_no_violations()
    {
        Assert.Empty(DataDirectoryPolicy.Check("a", State(), Root, DataDirectoryPolicy.TrustedSids));
        Assert.Empty(DataDirectoryPolicy.Check("a", State(owner: DataDirectoryPolicy.TrustedInstaller), Root, DataDirectoryPolicy.TrustedSids));
        Assert.Empty(DataDirectoryPolicy.Check("a", State(owner: DataDirectoryPolicy.Administrators,
            aces: new DataAce(FakeDataFileSystem.Users, false, 0x1F01FF, 0)), Root, DataDirectoryPolicy.TrustedSids)); // a deny entry is fine
    }

    public static TheoryData<string, DataViolationKind, bool> Cases => new()
    {
        { "owner-user", DataViolationKind.UntrustedOwner, false },
        { "owner-unknown", DataViolationKind.UntrustedOwner, false },
        { "ace-users", DataViolationKind.UntrustedAccess, false },
        { "null-dacl", DataViolationKind.NullDacl, false },
        { "reparse", DataViolationKind.ReparsePoint, true },
        { "other", DataViolationKind.NotDirectoryOrFile, true },
        { "hardlinks", DataViolationKind.HardLinks, true },
        { "outside", DataViolationKind.OutsidePath, true },
        { "final-unknown", DataViolationKind.OutsidePath, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_problem_is_reported_and_classified(string name, DataViolationKind kind, bool blocksRepair)
    {
        var state = name switch
        {
            "owner-user" => State(owner: FakeDataFileSystem.SomeUser),
            "owner-unknown" => State(owner: null),
            "ace-users" => State(aces: [.. FakeDataFileSystem.TrustedAces(), new DataAce(FakeDataFileSystem.Users, true, 0x1200A9, 0x10)]),
            "null-dacl" => State(daclPresent: false, aces: [new DataAce(DataDirectoryPolicy.LocalSystem, true, 1, 0)]),
            "reparse" => State(kind: DataObjectKind.ReparsePoint),
            "other" => State(kind: DataObjectKind.Other),
            "hardlinks" => State(links: 2),
            "outside" => State(final: TestPaths.Abs("/elsewhere/a")),
            _ => State(final: null),
        };
        var violation = Assert.Single(DataDirectoryPolicy.Check("a", state, Root, DataDirectoryPolicy.TrustedSids));
        Assert.Equal(kind, violation.Kind);
        Assert.Equal(blocksRepair, violation.BlocksRepair);
    }

    [Fact]
    public void A_folder_with_several_links_is_fine_but_a_file_is_not()
    {
        Assert.Empty(DataDirectoryPolicy.Check("a", State(kind: DataObjectKind.Directory, links: 3), Root, DataDirectoryPolicy.TrustedSids));
        Assert.Contains(DataDirectoryPolicy.Check("a", State(links: 3), Root, DataDirectoryPolicy.TrustedSids), v => v.Kind == DataViolationKind.HardLinks);
    }

    [Fact]
    public void Paths_compare_without_case()
    {
        Assert.Empty(DataDirectoryPolicy.Check("a", State(final: TestPaths.Abs("/PD/nexuspc/A")), Root, DataDirectoryPolicy.TrustedSids));
    }

    [Fact]
    public void Reparse_point_is_not_judged_by_its_security()
    {
        // Its descriptor belongs to the link, not to what it points at; it is rejected for being a link.
        var violation = Assert.Single(DataDirectoryPolicy.Check("a", State(kind: DataObjectKind.ReparsePoint, owner: FakeDataFileSystem.SomeUser), Root, DataDirectoryPolicy.TrustedSids));
        Assert.Equal(DataViolationKind.ReparsePoint, violation.Kind);
    }

    [Fact]
    public void Trusted_set_is_exactly_system_administrators_trustedinstaller()
    {
        Assert.Equal(3, DataDirectoryPolicy.TrustedSids.Count);
        Assert.Contains("S-1-5-18", (IReadOnlySet<string>)DataDirectoryPolicy.TrustedSids);
        Assert.Contains("S-1-5-32-544", (IReadOnlySet<string>)DataDirectoryPolicy.TrustedSids);
        Assert.Contains("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", (IReadOnlySet<string>)DataDirectoryPolicy.TrustedSids);
        Assert.DoesNotContain(FakeDataFileSystem.Users, (IReadOnlySet<string>)DataDirectoryPolicy.TrustedSids);
    }

    [Fact]
    public void Required_space_is_size_times_1_1_plus_50_MB()
    {
        Assert.Equal(50L * 1024 * 1024, DataDirectoryRepair.RequiredSpace(0));
        Assert.Equal(1100 + (50L * 1024 * 1024), DataDirectoryRepair.RequiredSpace(1000));
    }

    [Fact]
    public void Only_one_active_signed_in_session_which_is_ours_is_allowed()
    {
        InteractiveSession services = new(0, "Disconnected", null, "Services");
        InteractiveSession me = new(1, "Active", "owner", "Console");
        Assert.Null(InteractiveSessionPolicy.Refusal([services, me, new(65536, "Listen", null, "RDP-Tcp")], 1));
        Assert.NotNull(InteractiveSessionPolicy.Refusal([services, me, new(2, "Disconnected", "guest", "")], 1)); // a disconnected user still counts
        Assert.NotNull(InteractiveSessionPolicy.Refusal([services, me, new(3, "Active", "other", "RDP-Tcp#1")], 1));
        Assert.NotNull(InteractiveSessionPolicy.Refusal([services, me], 2)); // run from another session
        Assert.NotNull(InteractiveSessionPolicy.Refusal([services, me with { State = "Disconnected" }], 1));
        Assert.NotNull(InteractiveSessionPolicy.Refusal([services, me], null));
        Assert.NotNull(InteractiveSessionPolicy.Refusal([services], 0));
    }

    [Theory]
    [InlineData("check", "check", null, null)]
    [InlineData("repair", "repair", null, null)]
    [InlineData("repair --continue B", "continue", "B", null)]
    [InlineData("restore-acl B --data-dir E:\\ProgramData\\NexusPC", "restore-acl", "B", "E:\\ProgramData\\NexusPC")]
    [InlineData("check --data-dir /mnt/x/nexuspc", "check", null, "/mnt/x/nexuspc")]
    public void Datadir_arguments_are_parsed(string line, string verb, string? backup, string? dataDir)
    {
        var parsed = DataDirectoryCommandLine.Parse(line.Split(' '));
        Assert.NotNull(parsed);
        Assert.Equal(verb, parsed.Verb);
        Assert.Equal(backup, parsed.Backup);
        Assert.Equal(dataDir, parsed.DataDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("restore-acl")]
    [InlineData("check B")]
    [InlineData("repair --continue")]
    [InlineData("check --force")]
    [InlineData("delete")]
    public void Unknown_datadir_arguments_are_refused(string line)
    {
        Assert.Null(DataDirectoryCommandLine.Parse(line.Length == 0 ? [] : line.Split(' ')));
    }
}

public sealed class DataDirectoryRepairTests
{
    private static readonly string Root = TestPaths.Root;
    private static readonly string Plugins = Root + Rel("/OpenRGB/plugins");
    private readonly FakeDataFileSystem _fs = new();
    private readonly FixedTimeProvider _time = new(Shared.Now);

    public DataDirectoryRepairTests()
    {
        _fs.AddDirectory(Pd);
        _fs.AddDirectory(Root, FakeDataFileSystem.SomeUser).Protected = false;
        _fs.AddFile(Root + Sep + "config.json", """{"pcId":"x","trayPath":"C:\\evil\\tray.exe","certificateThumbprint":"ABCD"}""");
        _fs.AddFile(Root + Sep + "devices.json", """[{"DeviceId":"d","Name":"Phone\u0007","PublicKey":"AQID","Permissions":["power"],"PairedAt":"2026-01-01T00:00:00Z"}]""",
            aces: [.. FakeDataFileSystem.TrustedAces(), new DataAce(FakeDataFileSystem.Users, true, 0x1F01FF, 0x10)]);
        _fs.AddFile(Root + Sep + "audit.log", "line\n", owner: FakeDataFileSystem.SomeUser);
        _fs.AddFile(Root + Sep + "stranger.txt", "?");
        _fs.AddDirectory(Root + Sep + "OpenRGB");
        _fs.AddFile(Root + Rel("/OpenRGB/OpenRGB.json"), "{}");
        _fs.AddDirectory(Plugins);
        _fs.AddFile(Plugins + Sep + "evil.dll", "MZ");
    }

    private DataDirectoryRepair Repair(ScriptedConsole console) => new(_fs, console, _time);

    private static string? BackupOf(FakeDataFileSystem fs) => fs.Nodes.Keys.FirstOrDefault(k => k.StartsWith(Root + ".backup-", StringComparison.Ordinal) && !k[(Root.Length + 1)..].Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private string ContentsSnapshot() => string.Join("\n", _fs.Nodes.Where(n => n.Key.StartsWith(Root + Sep, StringComparison.Ordinal) && n.Value.Kind == DataObjectKind.File)
        .OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Key.Replace("plugins.disabled-20261114-100000", "plugins", StringComparison.Ordinal) + "=" + Convert.ToHexString(SHA256.HashData(n.Value.Content))));

    [Fact]
    public void Scan_reports_every_problem_and_changes_nothing()
    {
        var before = _fs.Snapshot(Root);
        var scan = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.Equal(before, _fs.Snapshot(Root));
        Assert.True(scan.Repairable);
        Assert.Contains(scan.Violations, v => v.RelativePath.Length == 0 && v.Kind == DataViolationKind.UntrustedOwner);
        Assert.Contains(scan.Violations, v => v.RelativePath == "devices.json" && v.Kind == DataViolationKind.UntrustedAccess && v.Detail.Contains(FakeDataFileSystem.Users, StringComparison.Ordinal));
        Assert.Contains(scan.Violations, v => v.RelativePath == "audit.log" && v.Kind == DataViolationKind.UntrustedOwner);
        Assert.Contains(scan.Violations, v => v.Kind == DataViolationKind.Plugins);
        Assert.Equal(_fs.Nodes.Keys.Count(k => k.StartsWith(Root, StringComparison.Ordinal)), scan.Plan.Count);
    }

    [Fact]
    public void Empty_plugins_folder_is_not_a_violation()
    {
        _fs.Nodes.Remove(Plugins + Sep + "evil.dll");
        var scan = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.DoesNotContain(scan.Violations, v => v.Kind == DataViolationKind.Plugins);
        Assert.Null(scan.Plugins); // empty: nothing to quarantine (owner decision 2)
    }

    [Fact]
    public void Clean_folder_passes_check()
    {
        var fs = new FakeDataFileSystem();
        fs.AddDirectory(Root).Protected = true;
        fs.AddFile(Root + Sep + "config.json", "{}", owner: DataDirectoryPolicy.LocalSystem);
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitOk, new DataDirectoryRepair(fs, console, _time).Check(Root));
    }

    [Theory]
    [InlineData("reparse")]
    [InlineData("hardlink")]
    [InlineData("outside")]
    [InlineData("other")]
    [InlineData("root-reparse")]
    public void Structural_problem_refuses_repair_and_nothing_is_created_or_changed(string problem)
    {
        switch (problem)
        {
            case "reparse":
                _fs.AddDirectory(Root + Rel("/OpenRGB/link")).Kind = DataObjectKind.ReparsePoint;
                _fs.AddFile(Root + Rel("/OpenRGB/link/target.txt"), "behind the link"); // must never be visited
                break;
            case "hardlink":
                _fs.Nodes[Root + Sep + "config.json"].Links = 2;
                break;
            case "outside":
                _fs.Nodes[Root + Rel("/OpenRGB/OpenRGB.json")].FinalOverride = Abs("/Windows/System32/x");
                break;
            case "other":
                _fs.AddFile(Root + Sep + "device", "").Kind = DataObjectKind.Other;
                break;
            default:
                _fs.Nodes[Root].Kind = DataObjectKind.ReparsePoint;
                break;
        }
        var opened = new List<string>();
        _fs.OnOpen = (path, _) => opened.Add(path);
        var before = _fs.Snapshot(Pd);
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Equal(before, _fs.Snapshot(Pd));
        Assert.Null(BackupOf(_fs));
        Assert.DoesNotContain(Root + Rel("/OpenRGB/link/target.txt"), opened);
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Contains("Nothing was changed", console.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_excludes_rejected_objects_and_does_not_enter_links()
    {
        _fs.AddDirectory(Root + Sep + "junction").Kind = DataObjectKind.ReparsePoint;
        _fs.AddFile(Root + Rel("/junction/inside"), "x");
        _fs.Nodes[Root + Sep + "stranger.txt"].Links = 2;
        var scan = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.False(scan.Repairable);
        Assert.DoesNotContain(scan.Plan, e => e.RelativePath == "junction" || e.RelativePath == "stranger.txt");
        Assert.DoesNotContain(scan.Entries, e => e.RelativePath.StartsWith(Rel("junction/"), StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_preconditions_or_missing_confirmation_change_nothing()
    {
        var before = _fs.Snapshot(Pd);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, ["The Nexus PC service is not stopped"]));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole("yes")).Repair(Root, []));
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole()).Repair(Root, []));
        Assert.Equal(before, _fs.Snapshot(Pd));
        Assert.Null(BackupOf(_fs));
    }

    [Fact]
    public void Contents_are_shown_before_the_confirmation()
    {
        var console = new ScriptedConsole("no");
        Repair(console).Repair(Root, []);
        var text = console.Text;
        Assert.Contains("\"Phone?\" paired", text, StringComparison.Ordinal); // control character replaced, never printed
        Assert.Contains("permissions power", text, StringComparison.Ordinal);
        Assert.Contains("key " + Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]))[..16], text, StringComparison.Ordinal);
        Assert.Contains(@"C:\evil\tray.exe", text, StringComparison.Ordinal);
        Assert.Contains("ABCD", text, StringComparison.Ordinal);
        Assert.Contains("OpenRGB plugins folder: 1 item(s)", text, StringComparison.Ordinal);
        Assert.Contains("stranger.txt", text, StringComparison.Ordinal);
        Assert.Contains("evil.dll", text, StringComparison.Ordinal);
        Assert.Contains("Устройства и разрешения", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Phone", StringComparison.Ordinal) < text.IndexOf("Type " + DataDirectoryRepair.ConfirmationWord, StringComparison.Ordinal));
    }

    [Fact]
    public void Not_enough_space_refuses_before_anything_is_created()
    {
        _fs.Available = DataDirectoryRepair.RequiredSpace(_fs.Nodes.Where(n => n.Key.StartsWith(Root + Sep, StringComparison.Ordinal)).Sum(n => (long)n.Value.Content.Length)) - 1;
        var before = _fs.Snapshot(Pd);
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Equal(before, _fs.Snapshot(Pd));
        Assert.Contains("Not enough free space", console.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void Copy_failing_at_file_k_keeps_an_incomplete_backup_and_the_folder_untouched(int k)
    {
        _fs.FailCreateFileAt = k;
        var before = _fs.Snapshot(Root);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        Assert.Equal(before, _fs.Snapshot(Root));
        Assert.Equal(0, _fs.SecurityChanges);
        Assert.Contains(_fs.Nodes.Keys, key => key.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal));
        Assert.DoesNotContain(_fs.Nodes.Keys, key => key.StartsWith(Root + DataDirectoryRepair.BackupInfix, StringComparison.Ordinal)
            && !key.Contains(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void Copy_with_a_different_hash_is_refused()
    {
        _fs.CorruptCopies = true;
        var before = _fs.Snapshot(Root);
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(console).Repair(Root, []));
        Assert.Equal(before, _fs.Snapshot(Root));
        Assert.Contains("SHA-256", console.Text, StringComparison.Ordinal);
        Assert.Contains(_fs.Nodes.Keys, key => key.EndsWith(DataDirectoryRepair.IncompleteSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void Full_repair_backs_up_then_fixes_access_and_disables_plugins()
    {
        var contents = ContentsSnapshot();
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(console).Repair(Root, []));

        var after = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids);
        Assert.True(after.Trusted, string.Join("\n", after.Violations));
        Assert.False(_fs.Nodes.ContainsKey(Plugins));
        Assert.True(_fs.Nodes.ContainsKey(Root + Rel("/OpenRGB/plugins.disabled-20261114-100000/evil.dll")));
        Assert.Equal(contents, ContentsSnapshot());

        var backup = BackupOf(_fs)!;
        Assert.True(_fs.Nodes[backup].Protected);
        var manifest = JsonDocument.Parse(_fs.ReadAllText(backup + Sep + DataDirectoryRepair.ManifestName)!);
        var files = manifest.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "file").ToList();
        Assert.Equal(6, files.Count);
        foreach (var file in files)
        {
            var copy = _fs.Nodes[backup + Rel("/data/") + file.GetProperty("path").GetString()];
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(copy.Content)));
        }
        Assert.Contains(DataDirectoryRepair.Fingerprint(_fs.ReadAllText(backup + Sep + DataDirectoryRepair.ManifestName)!), console.Text, StringComparison.Ordinal);
        var journal = JsonDocument.Parse(_fs.ReadAllText(backup + Sep + DataDirectoryRepair.JournalName)!);
        Assert.Equal(RepairJournal.Completed, journal.RootElement.GetProperty("state").GetString());
        Assert.NotNull(_fs.ReadAllText(backup + Sep + DataDirectoryRepair.OriginalSecurityName));
        Assert.Empty(Repair(new ScriptedConsole()).FindUnfinished(Root));
    }

    [Fact]
    public void Original_security_is_saved_by_identity_before_the_first_change()
    {
        var seenBeforeFirstChange = false;
        _fs.OnOpen = (_, access) =>
        {
            if (access == DataAccess.ChangeSecurity && _fs.SecurityChanges == 0)
            {
                var backup = BackupOf(_fs)!;
                seenBeforeFirstChange = _fs.ReadAllText(backup + Sep + DataDirectoryRepair.OriginalSecurityName) is not null
                    && _fs.ReadAllText(backup + Sep + DataDirectoryRepair.JournalName) is not null
                    && _fs.ReadAllText(backup + Sep + DataDirectoryRepair.ManifestName) is not null;
            }
        };
        var original = _fs.Nodes[Root + Sep + "devices.json"];
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        Assert.True(seenBeforeFirstChange);
        var saved = JsonDocument.Parse(_fs.ReadAllText(BackupOf(_fs)! + Sep + DataDirectoryRepair.OriginalSecurityName)!);
        var entry = saved.RootElement.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("path").GetString() == "devices.json");
        Assert.Equal(original.Id, entry.GetProperty("fileId").GetString());
        Assert.Equal(7UL, entry.GetProperty("volume").GetUInt64());
        Assert.Contains(FakeDataFileSystem.Users, entry.GetProperty("sddl").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Interrupted_repair_is_reported_and_can_be_continued(int k)
    {
        var contents = ContentsSnapshot();
        _fs.CrashAfterSecurityChanges = k;
        Assert.Throws<SimulatedCrashException>(() => Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        _fs.CrashAfterSecurityChanges = null;

        var check = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(check).Check(Root));
        Assert.Contains("Unfinished repair", check.Text, StringComparison.Ordinal);
        var backup = BackupOf(_fs)!;
        var second = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitRefused, Repair(second).Repair(Root, [])); // a new repair waits for the old one
        Assert.Contains("restore-acl", second.Text, StringComparison.Ordinal);

        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole()).Continue(Root, backup, []));
        Assert.True(DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids).Trusted);
        Assert.Equal(contents, ContentsSnapshot());
        Assert.Empty(Repair(new ScriptedConsole()).FindUnfinished(Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(null)]
    public void Restore_acl_puts_back_the_original_owner_and_access_without_touching_contents(int? crashAfter)
    {
        var originals = _fs.Nodes.Where(n => n.Key == Root || n.Key.StartsWith(Root + Sep, StringComparison.Ordinal))
            .ToDictionary(n => n.Value.Id, n => FakeDataFileSystem.Sddl(n.Value));
        var contents = ContentsSnapshot();
        _fs.CrashAfterSecurityChanges = crashAfter;
        if (crashAfter is null)
        {
            Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        }
        else
        {
            Assert.Throws<SimulatedCrashException>(() => Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        }
        _fs.CrashAfterSecurityChanges = null;
        var backup = BackupOf(_fs)!;

        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(console).RestoreAcl(Root, backup, []));
        foreach (var (key, node) in _fs.Nodes.Where(n => n.Key == Root || n.Key.StartsWith(Root + Sep, StringComparison.Ordinal)))
        {
            Assert.True(originals[node.Id] == FakeDataFileSystem.Sddl(node), key);
        }
        Assert.Equal(contents, ContentsSnapshot());
        Assert.True(_fs.Nodes.ContainsKey(backup)); // the backup is kept
        // Owner decision (QA-T4-R3-3, variant A): restore-acl does not finish the repair; it stays visible and blocking.
        Assert.Equal(RepairJournal.Restored, Assert.Single(Repair(new ScriptedConsole()).FindUnfinished(Root)).State);
    }

    [Fact]
    public void Restore_acl_skips_an_object_that_is_no_longer_the_same_and_keeps_the_backup()
    {
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        var backup = BackupOf(_fs)!;
        var replaced = _fs.Nodes[Root + Sep + "devices.json"];
        replaced.Id = _fs.NewId(); // deleted and recreated under the same name
        var replacedSddl = FakeDataFileSystem.Sddl(replaced);
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).RestoreAcl(Root, backup, []));
        Assert.Equal(replacedSddl, FakeDataFileSystem.Sddl(replaced));
        Assert.Contains("replaced by another object", console.Text, StringComparison.Ordinal);
        Assert.True(_fs.Nodes.ContainsKey(backup));
        Assert.NotEmpty(Repair(new ScriptedConsole()).FindUnfinished(Root)); // restore-failed stays visible
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("unreadable")]
    public void Restore_acl_fails_with_code_3_when_the_access_read_back_differs_from_the_saved_one(string how)
    {
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        var backup = BackupOf(_fs)!;
        var target = Root + Sep + "devices.json";
        _fs.ReadBack = (path, sddl) => path != target ? sddl : how == "changed" ? sddl + "x" : null;
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).RestoreAcl(Root, backup, []));
        Assert.Contains("devices.json: the access after the restore differs from the saved one", console.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Original access restored", console.Text, StringComparison.Ordinal);
        var unfinished = Assert.Single(Repair(new ScriptedConsole()).FindUnfinished(Root));
        Assert.Equal(RepairJournal.RestoreFailed, unfinished.State);
        Assert.True(_fs.Nodes.ContainsKey(backup));
    }

    [Fact]
    public void Restore_acl_that_loses_only_the_ai_flag_ends_with_an_incomplete_restore_warning()
    {
        const string saved = "O:S-1-5-21-1-2-3-1001G:SYD:AI(A;;0x1301bf;;;BU)(A;ID;FA;;;SY)(A;ID;FA;;;BA)";
        _fs.Nodes[Root + Sep + "audit.log"].RawSddl = saved;
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(new ScriptedConsole(DataDirectoryRepair.ConfirmationWord)).Repair(Root, []));
        var backup = BackupOf(_fs)!;
        _fs.ReadBack = (_, sddl) => sddl == saved ? sddl.Replace("D:AI(", "D:(", StringComparison.Ordinal) : sddl;
        var console = new ScriptedConsole();
        Assert.Equal(DataDirectoryRepair.ExitOk, Repair(console).RestoreAcl(Root, backup, []));
        Assert.Contains("restored INCOMPLETELY", console.Text, StringComparison.Ordinal);
        Assert.Contains("audit.log", console.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Original access restored", console.Text, StringComparison.Ordinal);
        var unfinished = Assert.Single(Repair(new ScriptedConsole()).FindUnfinished(Root));
        Assert.Equal(RepairJournal.Restored, unfinished.State);
        Assert.Contains("restore-acl: warning:", unfinished.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("hardlink")]
    [InlineData("reparse")]
    [InlineData("acl")]
    public void Object_changed_between_scan_and_change_stops_the_repair(string change)
    {
        var target = Root + Sep + "stranger.txt";
        var done = false;
        string? changedSddl = null;
        _fs.OnOpen = (path, access) =>
        {
            if (!done && path == target && access == DataAccess.ChangeSecurity)
            {
                done = true;
                var node = _fs.Nodes[path];
                switch (change)
                {
                    case "replaced": node.Id = _fs.NewId(); break;
                    case "hardlink": node.Links = 2; break;
                    case "reparse": node.Kind = DataObjectKind.ReparsePoint; break;
                    default: node.Aces.Add(new DataAce(FakeDataFileSystem.Users, true, 0x1F01FF, 0)); break;
                }
                changedSddl = FakeDataFileSystem.Sddl(node);
            }
        };
        var console = new ScriptedConsole(DataDirectoryRepair.ConfirmationWord);
        Assert.Equal(DataDirectoryRepair.ExitStopped, Repair(console).Repair(Root, []));
        Assert.True(done);
        Assert.Contains("Stopped at stranger.txt", console.Text, StringComparison.Ordinal);
        Assert.Equal(changedSddl, FakeDataFileSystem.Sddl(_fs.Nodes[target])); // the changed object was not touched
        Assert.True(_fs.Nodes.ContainsKey(Plugins)); // later steps did not run
        var journal = JsonDocument.Parse(_fs.ReadAllText(BackupOf(_fs)! + Sep + DataDirectoryRepair.JournalName)!);
        Assert.Equal(RepairJournal.Stopped, journal.RootElement.GetProperty("state").GetString());
        Assert.Contains(journal.RootElement.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("stranger.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Untrusted_report_names_the_object_kind_and_the_repair_command()
    {
        var scan = DataDirectoryScanner.Scan(_fs, Root, DataDirectoryPolicy.TrustedSids);
        var report = Nexus.Agent.Storage.AgentPaths.UntrustedReport(Root, scan.Violations);
        Assert.Contains("devices.json: UntrustedAccess", report, StringComparison.Ordinal);
        Assert.Contains(FakeDataFileSystem.SomeUser, report, StringComparison.Ordinal);
        Assert.Contains("datadir repair", report, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", report, StringComparison.Ordinal);
    }
}
