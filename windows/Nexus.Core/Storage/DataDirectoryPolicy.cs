using System.Collections.Frozen;
using System.Globalization;

namespace Nexus.Core.Storage;

// Trust decisions about the agent's data folder (C:\ProgramData\NexusPC): which objects the service may
// rely on, and which the repair command may touch. No Windows calls here: the file system is behind
// IDataFileSystem, so every decision is tested on any OS. Nexus.Windows.DataDirectoryFileSystem is the
// real implementation (handles opened with FILE_FLAG_OPEN_REPARSE_POINT, security read by handle).

public enum DataObjectKind
{
    Directory,
    File,
    /// <summary>Junction, symbolic link, mount point, cloud placeholder or any other reparse point.</summary>
    ReparsePoint,
    /// <summary>Anything that is neither a plain directory nor a plain file (device, offline file, ...).</summary>
    Other,
}

/// <summary>Identity of a file system object independent of its name: volume serial number and file id.</summary>
public readonly record struct DataObjectId(ulong Volume, string FileId)
{
    public override string ToString() => Volume.ToString("x16", CultureInfo.InvariantCulture) + ":" + FileId;
}

/// <summary>One access control entry of a DACL. Allow is true for every type that is not an access-denied type.</summary>
public sealed record DataAce(string Sid, bool Allow, uint Mask, int Flags);

/// <summary>What one open handle says about the object behind it (read once, at open).</summary>
public sealed record DataObjectState
{
    public required string FullPath { get; init; }
    public required DataObjectKind Kind { get; init; }
    public uint Attributes { get; init; }
    public uint ReparseTag { get; init; }
    public uint LinkCount { get; init; } = 1;
    /// <summary>GetFinalPathNameByHandle without the \\?\ prefix; null if it could not be read.</summary>
    public string? FinalPath { get; init; }
    public DataObjectId Id { get; init; }
    public long Size { get; init; }
    public DateTimeOffset LastWriteUtc { get; init; }
    public string? OwnerSid { get; init; }
    public string? GroupSid { get; init; }
    /// <summary>False for a NULL DACL (full access for everyone).</summary>
    public bool DaclPresent { get; init; } = true;
    public bool DaclProtected { get; init; }
    public IReadOnlyList<DataAce> Dacl { get; init; } = [];
    /// <summary>Owner, group and DACL as read (SDDL), kept so restore-acl can put them back.</summary>
    public string? SecurityDescriptor { get; init; }
}

public enum DataViolationKind
{
    NotDirectoryOrFile,
    ReparsePoint,
    HardLinks,
    OutsidePath,
    Unreadable,
    TooLarge,
    UntrustedOwner,
    NullDacl,
    UntrustedAccess,
    Plugins,
    Changed,
    /// <summary>The data folder itself inherits access from its parent (its DACL is not protected).</summary>
    InheritedAccess,
}

public sealed record DataViolation(string RelativePath, DataViolationKind Kind, string Detail)
{
    /// <summary>
    /// Structural problems: the repair command refuses and changes nothing (reparse points, hard links,
    /// unexpected object types, paths that resolve elsewhere, objects it cannot read). Owner, access
    /// entries and plugins are what repair fixes.
    /// </summary>
    public bool BlocksRepair => Kind is not (DataViolationKind.UntrustedOwner or DataViolationKind.NullDacl
        or DataViolationKind.UntrustedAccess or DataViolationKind.Plugins or DataViolationKind.InheritedAccess);

    /// <summary>Names come from an untrusted folder: printed only after DataText.Clean (console, event log).</summary>
    public override string ToString() =>
        $"{(RelativePath.Length == 0 ? "." : DataText.Clean(RelativePath))}: {Kind} — {DataText.Clean(Detail, 400)}";
}

public static class DataText
{
    /// <summary>
    /// Text from the untrusted folder made safe to show: control characters and invisible formatting
    /// characters (bidi overrides such as U+202E, isolates, zero-width joiners) are replaced by '?',
    /// and the length is bounded.
    /// </summary>
    public static string Clean(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maxLength + 1));
        foreach (var c in text)
        {
            if (builder.Length >= maxLength)
            {
                builder.Append('…');
                break;
            }
            var category = char.GetUnicodeCategory(c);
            builder.Append(char.IsControl(c) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? '?' : c);
        }
        return builder.ToString();
    }
}

public static class DataDirectoryPolicy
{
    public const string LocalSystem = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>The only owners and the only grantees allowed anywhere in the data folder.</summary>
    public static readonly FrozenSet<string> TrustedSids =
        FrozenSet.ToFrozenSet([LocalSystem, Administrators, TrustedInstaller], StringComparer.OrdinalIgnoreCase);

    /// <summary>OpenRGB loads every DLL in this folder into its process (SYSTEM, started by the service).</summary>
    public static readonly string PluginsPath = Path.Combine("OpenRGB", "plugins");

    public const uint FullControl = 0x001F01FF;

    // Paths on Windows are case-insensitive; the fake file system in tests uses the same rule.
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public static bool IsPluginsDirectory(string relativePath) => string.Equals(relativePath, PluginsPath, PathComparison);

    public static string ExpectedFinalPath(string rootFinalPath, string relativePath) =>
        relativePath.Length == 0 ? rootFinalPath : Path.Join(rootFinalPath, relativePath);

    public static bool SamePath(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, PathComparison);

    /// <summary>Type, hard links and resolved path: what must hold before anything may be read, copied or changed.</summary>
    public static IReadOnlyList<DataViolation> CheckStructure(string relativePath, DataObjectState state, string rootFinalPath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(state);
        var found = new List<DataViolation>();
        switch (state.Kind)
        {
            case DataObjectKind.ReparsePoint:
                found.Add(new(relativePath, DataViolationKind.ReparsePoint, string.Create(CultureInfo.InvariantCulture, $"reparse point (tag 0x{state.ReparseTag:x8})")));
                return found; // never looked into or behind
            case DataObjectKind.Other:
                found.Add(new(relativePath, DataViolationKind.NotDirectoryOrFile, string.Create(CultureInfo.InvariantCulture, $"neither a folder nor a plain file (attributes 0x{state.Attributes:x8})")));
                return found;
            case DataObjectKind.File when state.LinkCount != 1:
                found.Add(new(relativePath, DataViolationKind.HardLinks, string.Create(CultureInfo.InvariantCulture, $"{state.LinkCount} hard links")));
                break;
        }
        var expected = ExpectedFinalPath(rootFinalPath, relativePath);
        if (!SamePath(state.FinalPath, expected))
        {
            found.Add(new(relativePath, DataViolationKind.OutsidePath, $"resolves to {state.FinalPath ?? "(unknown)"} instead of {expected}"));
        }
        return found;
    }

    /// <summary>Owner and DACL: only trusted subjects may own the object or be granted access to it.</summary>
    public static IReadOnlyList<DataViolation> CheckSecurity(string relativePath, DataObjectState state, IReadOnlySet<string> trusted)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(trusted);
        var found = new List<DataViolation>();
        if (state.OwnerSid is null || !trusted.Contains(state.OwnerSid))
        {
            found.Add(new(relativePath, DataViolationKind.UntrustedOwner, "owner " + (state.OwnerSid ?? "(unknown)")));
        }
        if (!state.DaclPresent)
        {
            found.Add(new(relativePath, DataViolationKind.NullDacl, "no access list: everyone has full access"));
        }
        foreach (var ace in state.Dacl.Where(a => a.Allow && !trusted.Contains(a.Sid)))
        {
            found.Add(new(relativePath, DataViolationKind.UntrustedAccess, string.Create(CultureInfo.InvariantCulture, $"access allowed to {ace.Sid} (mask 0x{ace.Mask:x8}, flags 0x{ace.Flags:x2})")));
        }
        return found;
    }

    public static IReadOnlyList<DataViolation> Check(string relativePath, DataObjectState state, string rootFinalPath, IReadOnlySet<string> trusted)
    {
        var structure = CheckStructure(relativePath, state, rootFinalPath);
        if (state.Kind is DataObjectKind.ReparsePoint or DataObjectKind.Other)
        {
            return structure;
        }
        return [.. structure, .. CheckSecurity(relativePath, state, trusted)];
    }

    /// <summary>
    /// True when the object is in the state repair leaves it in: trusted owner, trusted grantees only;
    /// the root also with a protected DACL (nothing inherited from the parent folder).
    /// </summary>
    public static bool IsRepaired(DataObjectState state, bool isRoot)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CheckSecurity("", state, TrustedSids).Count == 0 && (!isRoot || state.DaclProtected);
    }

    // Rights that would let someone else delete, rename or re-permission what is inside a folder.
    private const uint DangerousOnParent = 0x00010000 /* DELETE */ | 0x00040000 /* WRITE_DAC */ | 0x00080000 /* WRITE_OWNER */
        | 0x00000040 /* FILE_DELETE_CHILD */ | 0x10000000 /* GENERIC_ALL */ | 0x40000000 /* GENERIC_WRITE */;

    private const int InheritOnlyAce = 0x08;

    /// <summary>
    /// The folder that holds the data folder and its backups (C:\ProgramData): trusted owner, and nobody
    /// else may delete, rename or re-permission what is in it. Creating new folders there is normal
    /// for ProgramData (Users may) and is handled by checking every backup before it is used.
    /// </summary>
    public static IReadOnlyList<DataViolation> CheckParent(DataObjectState state, IReadOnlySet<string> trusted)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(trusted);
        var found = new List<DataViolation>();
        const string where = "..";
        if (state.Kind != DataObjectKind.Directory)
        {
            found.Add(new(where, DataViolationKind.NotDirectoryOrFile, "the parent folder is not a plain folder"));
            return found;
        }
        if (state.OwnerSid is null || !trusted.Contains(state.OwnerSid))
        {
            found.Add(new(where, DataViolationKind.UntrustedOwner, "parent folder owner " + (state.OwnerSid ?? "(unknown)")));
        }
        if (!state.DaclPresent)
        {
            found.Add(new(where, DataViolationKind.NullDacl, "the parent folder has no access list"));
        }
        foreach (var ace in state.Dacl.Where(a => a.Allow && !trusted.Contains(a.Sid) && (a.Flags & InheritOnlyAce) == 0 && (a.Mask & DangerousOnParent) != 0))
        {
            found.Add(new(where, DataViolationKind.UntrustedAccess, string.Create(CultureInfo.InvariantCulture, $"{ace.Sid} may delete or re-permission items of the parent folder (mask 0x{ace.Mask:x8})")));
        }
        return found;
    }

    /// <summary>A path kept in a journal or a manifest: relative, inside the folder, no drive, no "..".</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (path is null)
        {
            return false;
        }
        if (path.Length == 0)
        {
            return true; // the folder itself
        }
        if (Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }
        var parts = path.Split('/', '\\');
        return parts.All(p => p.Length > 0 && p != "." && p != ".." && p.Trim() == p);
    }

    /// <summary>The only name the plugins folder may be renamed to.</summary>
    public static bool IsQuarantineName(string? name) =>
        name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, @"^plugins\.disabled-\d{8}-\d{6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>An object found by the scan. Plan entries (Accepted) passed every structural check. Children: entries listed inside a folder.</summary>
public sealed record DataScanEntry(string RelativePath, DataObjectState State, bool Accepted, int Children = 0);

public sealed record DataScan(string Root, string? RootFinalPath, IReadOnlyList<DataScanEntry> Entries, IReadOnlyList<DataViolation> Violations)
{
    public bool Trusted => Violations.Count == 0;

    /// <summary>Repair may proceed only when nothing structural was found anywhere.</summary>
    public bool Repairable => RootFinalPath is not null && !Violations.Any(v => v.BlocksRepair);

    /// <summary>Objects repair may copy and change, parents before children. Rejected objects are never part of it.</summary>
    public IReadOnlyList<DataScanEntry> Plan => [.. Entries.Where(e => e.Accepted)];

    /// <summary>OpenRGB\plugins with something inside (an empty one is what OpenRGB itself creates and is left alone).</summary>
    public DataScanEntry? Plugins => Entries.FirstOrDefault(e => e.Accepted && e.State.Kind == DataObjectKind.Directory && e.Children > 0 && DataDirectoryPolicy.IsPluginsDirectory(e.RelativePath));
}

/// <summary>Raised by IDataFileSystem when an object cannot be opened or read.</summary>
public sealed class DataAccessException(string message, Exception? inner = null, bool notFound = false) : IOException(message, inner)
{
    /// <summary>The object does not exist (any more): file or path not found.</summary>
    public bool NotFound { get; } = notFound;
}

public enum DataAccess
{
    /// <summary>Metadata and security only; shares read, write and delete (the service may be running).</summary>
    Inspect,
    /// <summary>Content as well; others may only read meanwhile.</summary>
    Read,
    /// <summary>Owner and DACL may be set through this handle; others may only read meanwhile.</summary>
    ChangeSecurity,
    /// <summary>ChangeSecurity plus rename.</summary>
    Rename,
}

/// <summary>Owner and DACL to put on one object, always through its handle, never by name and never recursively.</summary>
public abstract record DataSecurity
{
    private DataSecurity()
    {
    }

    /// <summary>The data folder itself: owner Administrators, protected DACL with SYSTEM and Administrators only.</summary>
    public sealed record ProtectedRoot : DataSecurity;

    /// <summary>Anything inside: owner Administrators, DACL made only of the entries inherited from the root.</summary>
    public sealed record InheritedFromRoot(bool IsDirectory) : DataSecurity;

    /// <summary>Owner, group and DACL exactly as saved before the repair (restore-acl).</summary>
    public sealed record Original(string Sddl) : DataSecurity;
}

public interface IDataObject : IDisposable
{
    DataObjectState State { get; }

    /// <summary>Content of a file, read through this very handle.</summary>
    Stream OpenContent();

    void SetSecurity(DataSecurity security);

    /// <summary>Renames through this handle (needs DataAccess.Rename).</summary>
    void Rename(string newFullPath);
}

/// <summary>The file system as the data folder checks and the repair command see it.</summary>
public interface IDataFileSystem
{
    /// <summary>Opens without following reparse points. Throws DataAccessException.</summary>
    IDataObject Open(string fullPath, DataAccess access);

    /// <summary>Names inside an open directory, enumerated through its handle.</summary>
    IReadOnlyList<string> ListChildren(IDataObject directory);

    /// <summary>Where the root must resolve to: the resolved parent folder plus the root's own name.</summary>
    string ExpectedRootFinalPath(string root);

    long AvailableBytes(string directory);

    bool DirectoryExists(string path);

    /// <summary>Full paths of the folders next to <paramref name="path"/> whose names start with its name plus <paramref name="suffixPrefix"/>.</summary>
    IReadOnlyList<string> Siblings(string path, string suffixPrefix);

    /// <summary>Creates a new folder with owner Administrators and a protected SYSTEM/Administrators DACL. Fails if it exists.</summary>
    void CreateProtectedDirectory(string path);

    /// <summary>Creates a new folder inside one made by CreateProtectedDirectory (inherits its access). Fails if it exists.</summary>
    void CreateDirectory(string path);

    /// <summary>A new file inside a backup folder. The caller flushes it with FlushToDisk.</summary>
    Stream CreateFile(string path);

    void FlushToDisk(Stream stream);

    /// <summary>Identity of a file made by CreateFile, read from its open handle.</summary>
    DataObjectId IdOf(Stream created);

    /// <summary>Writes a temporary file, flushes it to disk and renames it over <paramref name="path"/>.</summary>
    void WriteAllTextDurably(string path, string contents);

    string? ReadAllText(string path);

    /// <summary>Every object below a backup folder made by this program, relative paths.</summary>
    IReadOnlyList<string> ListTree(string path);

    void MoveDirectory(string source, string destination);
}

public static class DataDirectoryScanner
{
    public const int MaxDepth = 32;
    public const int MaxObjects = 100_000;

    /// <summary>
    /// Walks the whole folder through handles opened without following links. Reparse points and other
    /// rejected objects are reported and never entered. Changes nothing.
    /// </summary>
    public static DataScan Scan(IDataFileSystem fs, string root, IReadOnlySet<string> trusted) => Scan(fs, root, trusted, MaxDepth, MaxObjects);

    internal static DataScan Scan(IDataFileSystem fs, string root, IReadOnlySet<string> trusted, int maxDepth, int maxObjects)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(trusted);
        var entries = new List<DataScanEntry>();
        var violations = new List<DataViolation>();
        string expectedRoot;
        try
        {
            expectedRoot = fs.ExpectedRootFinalPath(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            violations.Add(new("", DataViolationKind.Unreadable, "the parent folder cannot be resolved: " + ex.Message));
            return new DataScan(root, null, entries, violations);
        }
        var limits = new Limits(maxDepth, maxObjects);
        Visit(fs, root, "", expectedRoot, trusted, 0, limits, entries, violations);
        var rootOk = entries.Count > 0 && entries[0].Accepted && entries[0].State.Kind == DataObjectKind.Directory;
        if (rootOk && !entries[0].State.DaclProtected)
        {
            // Service start, installer and repair alike: the folder must not take access from C:\ProgramData.
            violations.Insert(0, new("", DataViolationKind.InheritedAccess, "the folder's access list is not protected: it inherits access from the parent folder"));
        }
        return new DataScan(root, rootOk ? expectedRoot : null, entries, violations);
    }

    private sealed record Limits(int Depth, int Objects);

    private static void Visit(IDataFileSystem fs, string fullPath, string relativePath, string rootFinalPath, IReadOnlySet<string> trusted,
        int depth, Limits limits, List<DataScanEntry> entries, List<DataViolation> violations)
    {
        if (entries.Count >= limits.Objects)
        {
            violations.Add(new(relativePath, DataViolationKind.TooLarge, string.Create(CultureInfo.InvariantCulture, $"more than {limits.Objects} objects")));
            return;
        }
        IReadOnlyList<string> children = [];
        DataObjectState state;
        int index;
        try
        {
            using var handle = fs.Open(fullPath, DataAccess.Inspect);
            state = handle.State;
            var found = DataDirectoryPolicy.Check(relativePath, state, rootFinalPath, trusted);
            violations.AddRange(found);
            if (relativePath.Length == 0 && state.Kind == DataObjectKind.File)
            {
                violations.Add(new(relativePath, DataViolationKind.NotDirectoryOrFile, "the data folder is a file"));
            }
            var accepted = !found.Any(v => v.BlocksRepair) && !(relativePath.Length == 0 && state.Kind != DataObjectKind.Directory);
            index = entries.Count;
            entries.Add(new DataScanEntry(relativePath, state, accepted));
            if (!accepted || state.Kind != DataObjectKind.Directory)
            {
                return;
            }
            if (depth >= limits.Depth)
            {
                violations.Add(new(relativePath, DataViolationKind.TooLarge, string.Create(CultureInfo.InvariantCulture, $"nested deeper than {limits.Depth} levels")));
                return;
            }
            children = fs.ListChildren(handle);
        }
        catch (DataAccessException ex) when (ex.NotFound && relativePath.Length > 0)
        {
            // Gone between the folder listing and the open (a log rotated, a temporary file renamed): nothing
            // left to trust or distrust. It is not in the plan, so repair never touches it.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            violations.Add(new(relativePath, DataViolationKind.Unreadable, ex.Message));
            return;
        }
        entries[index] = entries[index] with { Children = children.Count };
        if (DataDirectoryPolicy.IsPluginsDirectory(relativePath) && children.Count > 0)
        {
            // OpenRGB itself creates an empty plugins folder when its window opens; anything inside would be loaded.
            violations.Add(new(relativePath, DataViolationKind.Plugins, string.Create(CultureInfo.InvariantCulture, $"OpenRGB plugins folder with {children.Count} item(s)")));
        }
        foreach (var name in children.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            {
                violations.Add(new(relativePath, DataViolationKind.NotDirectoryOrFile, "invalid name in folder listing"));
                continue;
            }
            Visit(fs, Path.Join(fullPath, name), relativePath.Length == 0 ? name : Path.Join(relativePath, name), rootFinalPath, trusted, depth + 1, limits, entries, violations);
        }
    }
}

/// <summary>A signed-in Windows session as WTSEnumerateSessions reports it.</summary>
/// <param name="QueryFailed">The user of this session could not be read: treated as refusal (fail closed).</param>
public sealed record InteractiveSession(int Id, string State, string? UserName, string? Station, bool QueryFailed = false);

public static class InteractiveSessionPolicy
{
    /// <summary>
    /// Repair and restore-acl run only when exactly one session has a signed-in user and it is the active
    /// session this command runs in. This lowers the risk of another signed-in user changing the folder
    /// between the scan and the changes; it does not remove it (services and scheduled tasks still run).
    /// Null when allowed, otherwise the reason.
    /// </summary>
    public static string? Refusal(IReadOnlyList<InteractiveSession> sessions, int? currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var signedIn = sessions.Where(s => !string.IsNullOrEmpty(s.UserName)).ToList();
        if (currentSessionId is null)
        {
            return "the session of this command is unknown";
        }
        if (sessions.FirstOrDefault(s => s.QueryFailed) is { } unknown)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the user of session #{unknown.Id} cannot be read");
        }
        if (signedIn.Count != 1)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{signedIn.Count} signed-in Windows sessions ({string.Join(", ", signedIn.Select(s => $"#{s.Id} {s.UserName} {s.State}"))}); sign out every other user first");
        }
        var only = signedIn[0];
        if (only.Id != currentSessionId || !string.Equals(only.State, "Active", StringComparison.Ordinal))
        {
            return string.Create(CultureInfo.InvariantCulture, $"run this command in the only active session (#{only.Id} {only.UserName} {only.State})");
        }
        return null;
    }
}

/// <summary>A running process as the presence check sees it; ImagePath null when it cannot be read.</summary>
public sealed record RunningProcess(int Id, string Name, string? ImagePath);

/// <summary>A pipe that exists; the server process when GetNamedPipeServerProcessId could tell.</summary>
public sealed record ExistingPipe(string Name, int? ServerProcessId = null, string? ServerImage = null);

/// <summary>
/// What the repair command needs from Windows besides files: the preconditions (checked again before every
/// change of access) and the service's start type, which is set to disabled for the whole repair.
/// </summary>
public interface IRepairEnvironment
{
    IReadOnlyList<string> PreconditionFailures();

    /// <summary>"auto", "delayed-auto", "demand" or "disabled"; null when the service is not installed.</summary>
    string? ServiceStartType();

    void SetServiceStartType(string startType);
}

public enum LockOutcome
{
    Acquired,
    /// <summary>Another datadir command, installation or removal of Nexus PC holds it.</summary>
    Busy,
    /// <summary>The protected lock could not be created or opened (fail closed).</summary>
    Unavailable,
}

/// <summary>The one lock shared by datadir commands, installation and removal (N1, N2).</summary>
public interface IDataDirectoryLockProvider
{
    LockOutcome TryAcquire(out IDisposable? held, out string? failure);
}

public static class ProtectedLockPolicy
{
    /// <summary>
    /// SA-T4-R7-1: the lock object found or made is used only if it is exactly what the lock creates: owner
    /// SYSTEM or Administrators, a protected DACL, and only access-allowed entries, each for SYSTEM or
    /// Administrators (no other SID, no deny entries). Null when acceptable, otherwise the reason.
    /// </summary>
    public static string? Refusal(string? ownerSid, bool daclPresent, bool daclProtected, IReadOnlyList<DataAce> dacl)
    {
        ArgumentNullException.ThrowIfNull(dacl);
        if (ownerSid is not (DataDirectoryPolicy.LocalSystem or DataDirectoryPolicy.Administrators))
        {
            return "the lock's owner is " + (ownerSid ?? "unknown") + ", not SYSTEM or Administrators";
        }
        if (!daclPresent)
        {
            return "the lock has no access list";
        }
        if (!daclProtected)
        {
            return "the lock's access list is not protected";
        }
        if (dacl.Count == 0)
        {
            return "the lock's access list is empty";
        }
        foreach (var ace in dacl)
        {
            if (!ace.Allow)
            {
                return "the lock's access list contains an entry that is not a plain allow entry (" + ace.Sid + ")";
            }
            if (ace.Sid is not (DataDirectoryPolicy.LocalSystem or DataDirectoryPolicy.Administrators))
            {
                return "the lock grants access to " + ace.Sid;
            }
        }
        return null;
    }
}

public static class ExclusiveRun
{
    public const string BusyText = "Refused: another Nexus PC datadir command, installation or removal is running on this PC. Nothing was changed; try again when it has finished.";
    public const string UnavailableText = "Refused: the protected Nexus PC lock could not be created or opened, so it cannot be made sure that nothing else changes the data folder meanwhile. Nothing was changed.";

    /// <summary>
    /// Runs <paramref name="action"/> while holding the lock, or refuses without running it: busy and
    /// unavailable are told apart, both return <paramref name="refusedCode"/> (fail closed).
    /// </summary>
    public static int Run(IDataDirectoryLockProvider locks, Action<string> say, int refusedCode, Func<int> action)
    {
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(say);
        ArgumentNullException.ThrowIfNull(action);
        var outcome = locks.TryAcquire(out var held, out var error);
        if (outcome != LockOutcome.Acquired || held is null)
        {
            say(outcome == LockOutcome.Busy ? BusyText : UnavailableText + (error is null ? "" : " (" + error + ")"));
            return refusedCode;
        }
        using (held)
        {
            return action();
        }
    }
}

/// <summary>
/// A plain named mutex: the portable building block (tests, other OSes). It is NOT protected against an
/// ordinary user taking the name first; on Windows the datadir commands and the installer use
/// Nexus.Windows.ProtectedDataDirectoryLock (a private namespace bound to Administrators) instead.
/// </summary>
public sealed class DataDirectoryLock : IDisposable
{
    private readonly Mutex _mutex;

    private DataDirectoryLock(Mutex mutex) => _mutex = mutex;

    /// <summary>The lock, or null when another datadir command holds it (or it cannot be taken).</summary>
    public static DataDirectoryLock? TryAcquire(string name)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
        try
        {
            if (mutex.WaitOne(0))
            {
                return new DataDirectoryLock(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-command: its journal says what is unfinished. The lock is ours now.
            return new DataDirectoryLock(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

/// <summary>No service, no further preconditions (tests of the plain repair logic, offline folders).</summary>
public sealed class NoRepairEnvironment : IRepairEnvironment
{
    public IReadOnlyList<string> PreconditionFailures() => [];

    public string? ServiceStartType() => null;

    public void SetServiceStartType(string startType) => throw new InvalidOperationException("no service");
}

public static class AgentPresencePolicy
{
    public static readonly IReadOnlyList<string> PipeNames = ["NexusPC.Admin", "NexusPC.Session"];

    /// <summary>
    /// datadir repair and restore-acl run only when no Nexus PC agent can touch the folder: neither of its
    /// pipes exists (an existing pipe counts even if it is busy or refuses this account), and no
    /// Nexus.Agent.exe or OpenRGB.exe from the installation folder runs (other than this process).
    /// A process whose path cannot be read counts as running (fail closed).
    /// </summary>
    public static IReadOnlyList<string> Refusals(IEnumerable<ExistingPipe> existingPipes, IEnumerable<RunningProcess> processes, int selfProcessId, string installRoot)
    {
        ArgumentNullException.ThrowIfNull(existingPipes);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(installRoot);
        var found = new List<string>();
        foreach (var pipe in existingPipes.Where(p => PipeNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var server = pipe.ServerProcessId is { } pid
                ? string.Create(CultureInfo.InvariantCulture, $"served by process {pid} ({DataText.Clean(pipe.ServerImage ?? "location unknown")})")
                : "server process unknown (the channel is busy or closed to this account)";
            found.Add($"A Nexus PC agent is running: the channel {pipe.Name} exists, {server}. Stop the service and close every Nexus.Agent window.");
        }
        var folder = Path.TrimEndingDirectorySeparator(installRoot) + "\\";
        foreach (var process in processes.Where(p => p.Id != selfProcessId))
        {
            if (!process.Name.Equals("Nexus.Agent", StringComparison.OrdinalIgnoreCase) && !process.Name.Equals("OpenRGB", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (process.ImagePath is null)
            {
                found.Add(string.Create(CultureInfo.InvariantCulture, $"{process.Name} (process {process.Id}) is running and its location cannot be read. Close it first."));
            }
            else if (process.ImagePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(string.Create(CultureInfo.InvariantCulture, $"{process.ImagePath} (process {process.Id}) is running. Close it first."));
            }
        }
        return found;
    }
}

/// <summary>Arguments of "datadir" (agent) and "--datadir-…" (installer): one parser for both.</summary>
public sealed record DataDirectoryCommandLine(string Verb, string? Backup, string? DataDirectory)
{
    public const string FolderName = "NexusPC";

    public static DataDirectoryCommandLine? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return null;
        }
        string? backup = null, dataDirectory = null;
        var verb = args[0];
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Count && dataDirectory is null:
                    dataDirectory = args[++i];
                    break;
                case "--continue" when verb == "repair" && i + 1 < args.Count && backup is null:
                    verb = "continue";
                    backup = args[++i];
                    break;
                case var path when verb is "restore-acl" or "finalize" && backup is null && !path.StartsWith("--", StringComparison.Ordinal):
                    backup = path;
                    break;
                default:
                    return null;
            }
        }
        // --data-dir names a data folder (for example on a disk attached to another PC), never any folder.
        if (dataDirectory is not null && !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dataDirectory.Replace('\\', '/'))), FolderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return verb switch
        {
            "check" or "repair" => new DataDirectoryCommandLine(verb, null, dataDirectory),
            "continue" or "restore-acl" or "finalize" when backup is not null => new DataDirectoryCommandLine(verb, backup, dataDirectory),
            _ => null,
        };
    }
}
