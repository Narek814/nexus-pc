using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Core.Storage;

/// <summary>Console of the elevated "Nexus.Agent datadir ..." command.</summary>
public interface IRepairConsole
{
    void WriteLine(string text);

    string? ReadLine();
}

public sealed record BackupManifestEntry(string Path, DataObjectKind Kind, long Size, string? Sha256, DateTimeOffset LastWriteUtc, ulong Volume, string FileId);

public sealed record BackupManifest(int Format, string Root, DateTimeOffset CreatedAt, IReadOnlyList<BackupManifestEntry> Entries);

/// <summary>Owner, group and DACL of one object before the repair, tied to the object's identity, not only its path.</summary>
public sealed record OriginalSecurityEntry(string Path, DataObjectKind Kind, ulong Volume, string FileId, string Sddl);

public sealed record OriginalSecurity(int Format, string Root, string RootFinalPath, DateTimeOffset SavedAt, IReadOnlyList<OriginalSecurityEntry> Entries);

public sealed class RepairStep
{
    public const string ProtectRoot = "protect-root";
    public const string Inherit = "inherit";
    public const string RenamePlugins = "rename-plugins";

    public required int Index { get; init; }
    /// <summary>Relative to the data folder ("" for the folder itself); never absolute, never "..".</summary>
    public required string Path { get; init; }
    public required DataObjectKind Kind { get; init; }
    public required string Action { get; init; }
    public required ulong Volume { get; init; }
    public required string FileId { get; init; }
    /// <summary>Only for rename-plugins: plugins.disabled-yyyyMMdd-HHmmss.</summary>
    public string? RenameTo { get; init; }
    public string Status { get; set; } = "pending";
}

public sealed class RepairJournal
{
    public const string Planned = "planned";
    public const string InProgress = "acl-in-progress";
    public const string Stopped = "stopped";
    public const string VerifyFailed = "verify-failed";
    public const string Completed = "completed";
    public const string Restored = "acl-restored";
    public const string RestoreFailed = "restore-failed";

    public int Format { get; init; } = 1;
    /// <summary>For matching only: operations always use the folder given on the command line.</summary>
    public required string Root { get; init; }
    public required string RootFinalPath { get; init; }
    public required string Backup { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string OriginalSecuritySha256 { get; init; }
    public string State { get; set; } = Planned;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<RepairStep> Steps { get; init; } = [];
    public List<string> Problems { get; init; } = [];

    /// <summary>Start type of the Nexus PC service before the repair (null: not installed), saved before it is disabled.</summary>
    public string? OriginalStartType { get; init; }
    /// <summary>The service was set to "disabled" by this repair and must stay so until the repair is finished.</summary>
    public bool ServiceDisabled { get; set; }
    public bool ServiceRestored { get; set; }

    /// <summary>Finished states: nothing is left half done.</summary>
    [JsonIgnore]
    public bool Finished => State is Completed or Restored;
}

/// <summary>A NexusPC.backup-* folder next to the data folder. Foreign: it fails the trust check and is never used.</summary>
public sealed record UnfinishedRepair(string Backup, string State, string Detail, bool Foreign = false);

/// <summary>Thrown when an object is no longer the one the scan accepted, or a copy is not identical.</summary>
public sealed class DataRepairException(string message) : Exception(message);

/// <summary>
/// "Nexus.Agent datadir check | repair | repair --continue | restore-acl": the decisions and their order.
/// Before the backup is complete and verified the data folder is only read. Changes are made only through
/// handles of objects that pass the same checks again, and every step is journaled so an interruption can
/// be continued or undone. Contents are never deleted, moved or rewritten.
/// </summary>
public sealed class DataDirectoryRepair(IDataFileSystem fs, IRepairConsole console, TimeProvider time, IRepairEnvironment? environment = null)
{
    public const string ConfirmationWord = "REPAIR";
    public const string BackupInfix = ".backup-";
    public const string IncompleteSuffix = ".incomplete";
    public const string DataFolder = "data";
    public const string ManifestName = "backup-manifest.json";
    public const string JournalName = "repair-journal.json";
    public const string OriginalSecurityName = "original-security.json";
    public const long SpaceReserve = 50L * 1024 * 1024;
    public const int MaxPreviewBytes = 1024 * 1024;

    public const int ExitOk = 0;
    /// <summary>Refused or problems found; the data folder was not changed.</summary>
    public const int ExitRefused = 1;
    /// <summary>Stopped after access changes began; the backup and the journal are kept.</summary>
    public const int ExitStopped = 3;

    private const string Done = "done";
    public const string DisabledStartType = "disabled";

    private readonly IRepairEnvironment _environment = environment ?? new NoRepairEnvironment();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowDuplicateProperties = false,
    };

    private static readonly string[] KnownTopLevel =
    [
        "config.json", "devices.json", "audit.log", "audit.log.1", "history.log", "history.log.1", "automations.json",
        "rgb.json", "agent-dev.pfx", "OpenRGB",
        "config.json.tmp", "devices.json.tmp", "automations.json.tmp", "rgb.json.tmp",
    ];

    private static readonly string[] CodeExtensions = [".dll", ".exe", ".sys", ".ps1", ".bat", ".cmd", ".vbs", ".js", ".scr", ".com", ".msi"];

    /// <summary>Minimum free space for the backup: folder size × 1.1 + 50 MB.</summary>
    public static long RequiredSpace(long folderBytes) => (long)Math.Ceiling(folderBytes * 1.1) + SpaceReserve;

    /// <summary>The structural problems that make repair refuse, by kind (for messages that name what was found).</summary>
    public static IReadOnlyList<DataViolationKind> BlockingKinds(DataScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var kinds = scan.Violations.Where(v => v.BlocksRepair).Select(v => v.Kind).Distinct().Order().ToList();
        if (kinds.Count == 0 && scan.RootFinalPath is null)
        {
            kinds.Add(DataViolationKind.Unreadable);
        }
        return kinds;
    }

    public static string BlockingSummary(DataScan scan) => string.Join(", ", BlockingKinds(scan).Select(kind => kind switch
    {
        DataViolationKind.ReparsePoint => "links (junctions, symbolic links, mount points or cloud placeholders)",
        DataViolationKind.HardLinks => "files with hard links",
        DataViolationKind.NotDirectoryOrFile => "objects that are neither folders nor plain files, or invalid names",
        DataViolationKind.OutsidePath => "objects that resolve outside the folder",
        DataViolationKind.Unreadable => "objects that cannot be read",
        DataViolationKind.TooLarge => "too many or too deeply nested objects",
        _ => kind.ToString(),
    }));

    public static string Fingerprint(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---------------------------------------------------------------- check

    public int Check(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            var scan = DataDirectoryScanner.Scan(fs, root, DataDirectoryPolicy.TrustedSids);
            var backups = FindUnfinished(root);
            Report(scan);
            ReportUnfinished(backups);
            var unfinished = backups.Count(b => !b.Foreign);
            if (scan.Trusted && unfinished == 0)
            {
                console.WriteLine("The data folder passes every check.");
                return ExitOk;
            }
            if (!scan.Trusted)
            {
                console.WriteLine(scan.Repairable
                    ? "\"datadir repair\" can fix owners, access and plugins after a verified backup."
                    : $"\"datadir repair\" refuses this folder ({BlockingSummary(scan)}): deal with these by hand first (docs/SETUP.md, \"Восстановление папки данных\").");
            }
            return ExitRefused;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.WriteLine("The check could not finish: " + ex.Message);
            return ExitRefused;
        }
    }

    /// <summary>
    /// Backups next to the data folder that need attention: trusted ones with an unfinished journal, and
    /// foreign ones (anything named like a backup that fails the trust check — reported, never used, and
    /// they never block a new repair).
    /// </summary>
    public IReadOnlyList<UnfinishedRepair> FindUnfinished(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<UnfinishedRepair>();
        foreach (var backup in fs.Siblings(root, BackupInfix).Order(StringComparer.Ordinal))
        {
            if (backup.EndsWith(IncompleteSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue; // a failed backup: the data folder was never changed
            }
            if (BackupDistrust(backup) is { } distrust)
            {
                found.Add(new UnfinishedRepair(backup, "foreign", distrust, Foreign: true));
                continue;
            }
            var journal = TryLoadJournal(backup, out var error);
            if (journal is null)
            {
                if (error is not null)
                {
                    found.Add(new UnfinishedRepair(backup, "unreadable", error));
                }
                continue; // a backup without a journal: stopped before any change
            }
            if (!journal.Finished)
            {
                found.Add(new UnfinishedRepair(backup, journal.State, string.Join("; ", journal.Problems)));
            }
        }
        return found;
    }

    /// <summary>
    /// A backup is trusted only if the whole folder passes the same check as the data folder (no links,
    /// trusted owners and grantees, journal files included) and its root is owned by Administrators or
    /// SYSTEM with a protected DACL — what CreateProtectedDirectory makes. Null when trusted.
    /// </summary>
    private string? BackupDistrust(string backup)
    {
        var scan = DataDirectoryScanner.Scan(fs, backup, DataDirectoryPolicy.TrustedSids);
        if (scan.Violations.Count > 0)
        {
            return "foreign folder, not used: " + scan.Violations[0];
        }
        var root = scan.Entries[0].State;
        if (root.OwnerSid is not (DataDirectoryPolicy.Administrators or DataDirectoryPolicy.LocalSystem) || !root.DaclProtected)
        {
            return "foreign folder, not used: not created by the repair command (owner or inherited access)";
        }
        return null;
    }

    // ---------------------------------------------------------------- repair

    public int Repair(string root, IReadOnlyList<string> preconditionFailures)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(preconditionFailures);
        string? backup = null;
        (RepairJournal Journal, OriginalSecurity Original) plan;
        try
        {
            // Р1. Preconditions.
            if (Refuse(preconditionFailures))
            {
                return ExitRefused;
            }
            var backups = FindUnfinished(root);
            ReportUnfinished(backups);
            if (backups.Any(b => !b.Foreign))
            {
                console.WriteLine("Nothing was changed. Finish or undo the earlier repair first.");
                return ExitRefused;
            }

            // Р2. Full scan without following links, and the folder the backup goes to.
            var scan = DataDirectoryScanner.Scan(fs, root, DataDirectoryPolicy.TrustedSids);
            Report(scan);
            if (!scan.Repairable)
            {
                console.WriteLine($"Repair refused ({BlockingSummary(scan)}). Nothing was changed.");
                return ExitRefused;
            }
            if (scan.Trusted && scan.Plugins is null)
            {
                console.WriteLine("Nothing to repair. Nothing was changed.");
                return ExitOk;
            }
            if (scan.Plan.FirstOrDefault(e => e.State.SecurityDescriptor is null) is { } unreadable)
            {
                console.WriteLine($"Repair refused: the access list of {Display(unreadable.RelativePath)} cannot be saved. Nothing was changed.");
                return ExitRefused;
            }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) ?? root;
            using (var parentObject = fs.Open(parent, DataAccess.Inspect))
            {
                var parentProblems = DataDirectoryPolicy.CheckParent(parentObject.State, DataDirectoryPolicy.TrustedSids);
                if (parentProblems.Count > 0)
                {
                    foreach (var problem in parentProblems)
                    {
                        console.WriteLine("  " + problem);
                    }
                    console.WriteLine($"Repair refused: the folder {parent} where the backup would go is not protected. Nothing was changed.");
                    return ExitRefused;
                }
            }
            var rootFinal = scan.RootFinalPath!;

            // Р3. What is in the folder (fixing access does not make it trustworthy), with the SHA-256 of every file as shown.
            var (shown, previews) = HashFiles(scan);
            ShowContents(scan, previews);
            console.WriteLine($"Type {ConfirmationWord} to back up the folder and fix its access, anything else to cancel:");
            if (!string.Equals(console.ReadLine()?.Trim(), ConfirmationWord, StringComparison.Ordinal))
            {
                console.WriteLine("Cancelled. Nothing was changed.");
                return ExitRefused;
            }
            // The answer may have taken a while: everything that must hold is checked again.
            if (Refuse(_environment.PreconditionFailures()))
            {
                return ExitRefused;
            }

            // Р4. Free space before anything is created.
            var bytes = scan.Plan.Where(e => e.State.Kind == DataObjectKind.File).Sum(e => e.State.Size);
            var required = RequiredSpace(bytes);
            var available = fs.AvailableBytes(parent);
            if (available < required)
            {
                console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Not enough free space for the backup: {available / 1024 / 1024} MB free, {required / 1024 / 1024} MB needed. Nothing was created or changed."));
                return ExitRefused;
            }

            // Р5. Backup, copied through handles and verified by SHA-256.
            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            // A random suffix: the name cannot be guessed (and pre-created) from the time alone.
            var candidate = Path.TrimEndingDirectorySeparator(root) + BackupInfix + stamp + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            if (fs.DirectoryExists(candidate) || fs.DirectoryExists(candidate + IncompleteSuffix))
            {
                console.WriteLine("A backup with this time already exists; try again in a minute. Nothing was changed.");
                return ExitRefused;
            }
            fs.CreateProtectedDirectory(candidate);
            backup = candidate;
            var manifestSha = Backup(scan, backup, shown);
            console.WriteLine($"Backup verified: {backup}");
            console.WriteLine($"SHA-256 of {ManifestName}: {manifestSha}");

            // Р6. Original owner, group and DACL by object identity, then the journal — before the first change.
            plan = Plan(scan, root, rootFinal, backup, stamp, manifestSha);
            console.WriteLine($"Original access saved: {Path.Join(backup, OriginalSecurityName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DataRepairException or JsonException or InvalidOperationException)
        {
            console.WriteLine("Repair refused: " + ex.Message);
            if (backup is not null)
            {
                MarkIncomplete(backup);
            }
            console.WriteLine("The data folder was not changed.");
            return ExitRefused;
        }

        // Р7 and Р8.
        return Apply(root, plan.Journal, plan.Original, backup);
    }

    /// <summary>Р9: continues an interrupted repair. Every step checks its object again; done steps are skipped.</summary>
    public int Continue(string root, string backup, IReadOnlyList<string> preconditionFailures)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(preconditionFailures);
        RepairJournal? journal;
        OriginalSecurity? original;
        try
        {
            if (Refuse(preconditionFailures) || (journal = LoadForResume(root, backup)) is null || (original = LoadOriginal(root, backup, journal)) is null)
            {
                return ExitRefused;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.WriteLine("Refused: " + ex.Message + " Nothing was changed.");
            return ExitRefused;
        }
        if (journal.State == RepairJournal.Completed)
        {
            console.WriteLine("This repair is already completed.");
            return ExitOk;
        }
        if (journal.State is RepairJournal.Restored or RepairJournal.RestoreFailed)
        {
            console.WriteLine("The original access of this repair was restored (or restoring was attempted); start a new repair instead. Nothing was changed.");
            return ExitRefused;
        }
        return Apply(root, journal, original, backup);
    }

    /// <summary>Р9: puts back the saved owner, group and DACL of every object that is still the same object. Contents are not touched.</summary>
    public int RestoreAcl(string root, string backup, IReadOnlyList<string> preconditionFailures)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(preconditionFailures);
        RepairJournal? journal;
        OriginalSecurity? original;
        try
        {
            if (Refuse(preconditionFailures) || (journal = LoadForResume(root, backup)) is null || (original = LoadOriginal(root, backup, journal)) is null)
            {
                return ExitRefused;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.WriteLine("Refused: " + ex.Message + " Nothing was changed.");
            return ExitRefused;
        }
        var rename = journal.Steps.FirstOrDefault(s => s.Action == RepairStep.RenamePlugins && s.Status == Done);
        var problems = new List<string>();
        // Children before parents, the root last: until the end the root keeps the repaired, protected access.
        foreach (var entry in original.Entries.OrderByDescending(e => e.Path.Length).ThenByDescending(e => e.Path, StringComparer.Ordinal))
        {
            var relative = MapRenamed(entry.Path, rename);
            var preconditions = _environment.PreconditionFailures();
            if (preconditions.Count > 0)
            {
                problems.Add("stopped, preconditions no longer hold: " + string.Join("; ", preconditions));
                break;
            }
            try
            {
                using var obj = fs.Open(FullPath(root, relative), DataAccess.ChangeSecurity);
                var mismatch = Mismatch(obj.State, entry.Kind, entry.Volume, entry.FileId, DataDirectoryPolicy.ExpectedFinalPath(journal.RootFinalPath, relative));
                if (mismatch is not null)
                {
                    problems.Add($"{Display(relative)}: {mismatch}; left as is");
                    continue;
                }
                obj.SetSecurity(new DataSecurity.Original(entry.Sddl));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Display(relative)}: {ex.Message}");
            }
        }
        if (problems.Count == 0)
        {
            // Undo is complete only with the start type the repair changed. The folder is back to its earlier,
            // untrusted access, so the service start check still refuses it until a new repair succeeds.
            try
            {
                RestoreStartType(journal, backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add("the service start type could not be put back: " + ex.Message);
            }
        }
        journal.State = problems.Count == 0 ? RepairJournal.Restored : RepairJournal.RestoreFailed;
        journal.Problems.AddRange(problems.Select(p => "restore-acl: " + p));
        var saved = TrySave(journal, backup);
        foreach (var problem in problems)
        {
            console.WriteLine("  " + problem);
        }
        if (problems.Count == 0 && saved)
        {
            console.WriteLine($"Original access restored from {backup}. Contents were not changed. The backup is kept. The service was not started.");
            if (journal.ServiceRestored)
            {
                console.WriteLine($"The service start type is back to \"{journal.OriginalStartType}\"; the service still refuses to start while the folder fails its check.");
            }
            return ExitOk;
        }
        console.WriteLine($"Original access was not restored for {problems.Count} object(s){(saved ? "" : " and the journal could not be updated")}. The backup is kept: {backup}. The service was not started and stays disabled; run restore-acl again.");
        return ExitStopped;
    }

    // ---------------------------------------------------------------- steps

    private bool Refuse(IReadOnlyList<string> preconditionFailures)
    {
        if (preconditionFailures.Count == 0)
        {
            return false;
        }
        foreach (var failure in preconditionFailures)
        {
            console.WriteLine("  " + failure);
        }
        console.WriteLine("Refused. Nothing was changed.");
        return true;
    }

    /// <summary>Р3: SHA-256 of every file of the plan, read through its handle when the contents are shown.</summary>
    /// <remarks>
    /// devices.json and config.json are read once into memory: what is shown and what is hashed are the same
    /// bytes, so the file cannot be swapped between showing it and fixing its SHA-256.
    /// </remarks>
    private (Dictionary<string, string> Hashes, Dictionary<string, string> Previews) HashFiles(DataScan scan)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in scan.Plan.Where(e => e.State.Kind == DataObjectKind.File))
        {
            using var obj = fs.Open(entry.State.FullPath, DataAccess.Read);
            if (Mismatch(obj.State, entry.State.Kind, entry.State.Id.Volume, entry.State.Id.FileId, entry.State.FinalPath!) is { } changed)
            {
                throw new DataRepairException($"{Display(entry.RelativePath)}: {changed}");
            }
            using var content = obj.OpenContent();
            if (PreviewNames.Contains(entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                if (obj.State.Size > MaxPreviewBytes)
                {
                    previews[entry.RelativePath] = "!too large to show";
                    hashes[entry.RelativePath] = Convert.ToHexStringLower(SHA256.HashData(content));
                    continue;
                }
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                var bytes = buffer.ToArray();
                hashes[entry.RelativePath] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                previews[entry.RelativePath] = Encoding.UTF8.GetString(bytes);
                continue;
            }
            hashes[entry.RelativePath] = Convert.ToHexStringLower(SHA256.HashData(content));
        }
        return (hashes, previews);
    }

    private static readonly string[] PreviewNames = ["devices.json", "config.json"];

    /// <summary>
    /// Р5: copies only accepted objects, hashing the source through its handle and the copy after it is on
    /// disk; every source must still have the hash it had when the contents were shown.
    /// </summary>
    private string Backup(DataScan scan, string backup, Dictionary<string, string> shown)
    {
        var data = Path.Join(backup, DataFolder);
        fs.CreateDirectory(data);
        var backupFinal = fs.ExpectedRootFinalPath(backup);
        var entries = new List<BackupManifestEntry>();
        foreach (var entry in scan.Plan.Where(e => e.RelativePath.Length > 0))
        {
            var target = Path.Join(data, entry.RelativePath);
            var state = entry.State;
            if (state.Kind == DataObjectKind.Directory)
            {
                fs.CreateDirectory(target);
                entries.Add(new BackupManifestEntry(entry.RelativePath, DataObjectKind.Directory, 0, null, state.LastWriteUtc, state.Id.Volume, state.Id.FileId));
                continue;
            }
            string sourceHash;
            long copied;
            DataObjectId createdId;
            using (var source = fs.Open(state.FullPath, DataAccess.Read))
            {
                if (Mismatch(source.State, state.Kind, state.Id.Volume, state.Id.FileId, state.FinalPath!) is { } changed)
                {
                    throw new DataRepairException($"{Display(entry.RelativePath)}: {changed}");
                }
                using var content = source.OpenContent();
                using var copy = fs.CreateFile(target);
                createdId = fs.IdOf(copy);
                (sourceHash, copied) = CopyHashing(content, copy);
                fs.FlushToDisk(copy);
                if (copied != source.State.Size || source.State.Size != state.Size)
                {
                    throw new DataRepairException($"{Display(entry.RelativePath)}: the size changed during the copy");
                }
            }
            if (!shown.TryGetValue(entry.RelativePath, out var expected) || !string.Equals(expected, sourceHash, StringComparison.Ordinal))
            {
                throw new DataRepairException($"{Display(entry.RelativePath)}: the contents changed after they were shown (SHA-256)");
            }
            // The copy is read back through a handle opened without following links, and must be the file just made.
            string copyHash;
            using (var written = fs.Open(target, DataAccess.Read))
            {
                var replaced = Mismatch(written.State, DataObjectKind.File, createdId.Volume, createdId.FileId,
                    DataDirectoryPolicy.ExpectedFinalPath(backupFinal, Path.Join(DataFolder, entry.RelativePath)));
                if (replaced is not null)
                {
                    throw new DataRepairException($"{Display(entry.RelativePath)}: the copy is not the file that was written ({replaced})");
                }
                using var content = written.OpenContent();
                copyHash = Convert.ToHexStringLower(SHA256.HashData(content));
            }
            if (!string.Equals(sourceHash, copyHash, StringComparison.Ordinal))
            {
                throw new DataRepairException($"{Display(entry.RelativePath)}: the copy differs from the original (SHA-256)");
            }
            entries.Add(new BackupManifestEntry(entry.RelativePath, DataObjectKind.File, copied, sourceHash, state.LastWriteUtc, state.Id.Volume, state.Id.FileId));
        }

        var manifest = new BackupManifest(1, scan.Root, time.GetUtcNow(), entries);
        var path = Path.Join(backup, ManifestName);
        fs.WriteAllTextDurably(path, JsonSerializer.Serialize(manifest, Json));
        var readBack = fs.ReadAllText(path) ?? throw new DataRepairException("the manifest cannot be read back");
        var parsed = JsonSerializer.Deserialize<BackupManifest>(readBack, Json) ?? throw new DataRepairException("the manifest is empty");
        if (!parsed.Entries.SequenceEqual(entries))
        {
            throw new DataRepairException("the manifest read back differs from the one written");
        }
        var copiedObjects = fs.ListTree(data).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var expectedObjects = entries.Select(e => e.Path).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!copiedObjects.SequenceEqual(expectedObjects, StringComparer.OrdinalIgnoreCase))
        {
            throw new DataRepairException(string.Create(CultureInfo.InvariantCulture, $"the backup has {copiedObjects.Count} objects, the folder {expectedObjects.Count}"));
        }
        return Fingerprint(readBack);
    }

    private static (string Sha256, long Bytes) CopyHashing(Stream source, Stream target)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
            total += read;
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    private void MarkIncomplete(string backup)
    {
        try
        {
            fs.MoveDirectory(backup, backup + IncompleteSuffix);
            console.WriteLine($"The partial backup is kept as {backup + IncompleteSuffix}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.WriteLine($"The partial backup {backup} could not be renamed ({ex.Message}); it is kept as is.");
        }
    }

    /// <summary>Р6: saves the original security of every planned object, then the journal with the steps.</summary>
    private (RepairJournal, OriginalSecurity) Plan(DataScan scan, string root, string rootFinal, string backup, string stamp, string manifestSha)
    {
        var original = new OriginalSecurity(1, root, rootFinal, time.GetUtcNow(),
            [.. scan.Plan.Select(e => new OriginalSecurityEntry(e.RelativePath, e.State.Kind, e.State.Id.Volume, e.State.Id.FileId, e.State.SecurityDescriptor!))]);
        var originalPath = Path.Join(backup, OriginalSecurityName);
        fs.WriteAllTextDurably(originalPath, JsonSerializer.Serialize(original, Json));
        var originalText = fs.ReadAllText(originalPath) ?? throw new DataRepairException("the saved access cannot be read back");
        var parsed = JsonSerializer.Deserialize<OriginalSecurity>(originalText, Json);
        if (parsed is null || !parsed.Entries.SequenceEqual(original.Entries))
        {
            throw new DataRepairException("the saved access read back differs from the one written");
        }

        var steps = new List<RepairStep>();
        foreach (var entry in scan.Plan)
        {
            steps.Add(new RepairStep
            {
                Index = steps.Count,
                Path = entry.RelativePath,
                Kind = entry.State.Kind,
                Action = entry.RelativePath.Length == 0 ? RepairStep.ProtectRoot : RepairStep.Inherit,
                Volume = entry.State.Id.Volume,
                FileId = entry.State.Id.FileId,
            });
        }
        if (scan.Plugins is { } plugins)
        {
            steps.Add(new RepairStep
            {
                Index = steps.Count,
                Path = plugins.RelativePath,
                Kind = DataObjectKind.Directory,
                Action = RepairStep.RenamePlugins,
                Volume = plugins.State.Id.Volume,
                FileId = plugins.State.Id.FileId,
                RenameTo = "plugins.disabled-" + stamp,
            });
        }
        var journal = new RepairJournal
        {
            Root = root,
            RootFinalPath = rootFinal,
            Backup = backup,
            StartedAt = time.GetUtcNow(),
            ManifestSha256 = manifestSha,
            OriginalSecuritySha256 = Fingerprint(originalText),
            Steps = steps,
            // Saved before the service is disabled; put back only after a verified repair (or restore-acl).
            OriginalStartType = _environment.ServiceStartType(),
        };
        Save(journal, backup);
        return (journal, parsed);
    }

    /// <summary>Р7 (owner and DACL by handle, after checking each object again) and Р8 (verification).</summary>
    private int Apply(string root, RepairJournal journal, OriginalSecurity original, string backup)
    {
        var saved = original.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        try
        {
            journal.State = RepairJournal.InProgress;
            Save(journal, backup);
            // No automatic start in an intermediate state: the service stays disabled until the repair is finished.
            if (journal.OriginalStartType is not null && journal.OriginalStartType != DisabledStartType && !journal.ServiceDisabled)
            {
                _environment.SetServiceStartType(DisabledStartType);
                journal.ServiceDisabled = true;
                Save(journal, backup);
                console.WriteLine($"The Nexus PC service is disabled for the repair (it was \"{journal.OriginalStartType}\").");
            }
            foreach (var step in journal.Steps.Where(s => s.Status != Done))
            {
                var preconditions = _environment.PreconditionFailures();
                var problem = preconditions.Count > 0
                    ? "preconditions no longer hold: " + string.Join("; ", preconditions)
                    : step.Action == RepairStep.RenamePlugins ? RenameStep(root, journal, step) : SecurityStep(root, journal, step, saved);
                if (problem is not null)
                {
                    journal.State = RepairJournal.Stopped;
                    journal.Problems.Add($"step {step.Index.ToString(CultureInfo.InvariantCulture)} {Display(step.Path)}: {problem}");
                    TrySave(journal, backup);
                    console.WriteLine($"Stopped at {Display(step.Path)}: {problem}");
                    StoppedAdvice(backup);
                    return ExitStopped;
                }
                step.Status = Done;
                Save(journal, backup);
            }

            var failures = Verify(root, journal, backup);
            if (failures.Count > 0)
            {
                journal.State = RepairJournal.VerifyFailed;
                journal.Problems.AddRange(failures);
                TrySave(journal, backup);
                foreach (var failure in failures)
                {
                    console.WriteLine("  " + failure);
                }
                console.WriteLine("The result did not pass the final check.");
                StoppedAdvice(backup);
                return ExitStopped;
            }
            RestoreStartType(journal, backup); // only now: access fixed and verified
            journal.State = RepairJournal.Completed;
            Save(journal, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            console.WriteLine("Stopped: " + ex.Message);
            StoppedAdvice(backup);
            return ExitStopped;
        }
        console.WriteLine("Repair completed: access fixed, every file matches the backup manifest. The backup is kept: " + backup);
        if (journal.ServiceRestored)
        {
            console.WriteLine($"The service start type is back to \"{journal.OriginalStartType}\".");
        }
        console.WriteLine("Check the paired devices in the tray (\"Устройства и разрешения\") and remove any you do not recognise, then start the service.");
        return ExitOk;
    }

    private void RestoreStartType(RepairJournal journal, string backup)
    {
        if (!journal.ServiceDisabled || journal.ServiceRestored || journal.OriginalStartType is null)
        {
            return;
        }
        _environment.SetServiceStartType(journal.OriginalStartType);
        journal.ServiceRestored = true;
        Save(journal, backup);
    }

    private void StoppedAdvice(string backup)
    {
        console.WriteLine($"Nothing else was changed. The backup and the journal are kept: {backup}.");
        console.WriteLine("The service was not started and stays disabled until the repair is finished; it also refuses to start while this repair is unfinished.");
        console.WriteLine($"Continue after checking: datadir repair --continue \"{backup}\"   Undo: datadir restore-acl \"{backup}\"");
    }

    private string? SecurityStep(string root, RepairJournal journal, RepairStep step, Dictionary<string, OriginalSecurityEntry> saved)
    {
        if (!saved.TryGetValue(step.Path, out var original) || original.Volume != step.Volume || !string.Equals(original.FileId, step.FileId, StringComparison.Ordinal))
        {
            return "no saved access for this object";
        }
        try
        {
            using var obj = fs.Open(FullPath(root, step.Path), DataAccess.ChangeSecurity);
            if (Mismatch(obj.State, step.Kind, step.Volume, step.FileId, DataDirectoryPolicy.ExpectedFinalPath(journal.RootFinalPath, step.Path)) is { } changed)
            {
                return changed;
            }
            // Either untouched since the scan, or already set by this step before an interruption.
            if (!string.Equals(obj.State.SecurityDescriptor, original.Sddl, StringComparison.Ordinal) && !DataDirectoryPolicy.IsRepaired(obj.State, step.Path.Length == 0))
            {
                return "its access changed since the scan";
            }
            obj.SetSecurity(step.Action == RepairStep.ProtectRoot
                ? new DataSecurity.ProtectedRoot()
                : new DataSecurity.InheritedFromRoot(step.Kind == DataObjectKind.Directory));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private string? RenameStep(string root, RepairJournal journal, RepairStep step)
    {
        var parentRelative = Path.GetDirectoryName(step.Path)!;
        var from = FullPath(root, step.Path);
        var to = Path.Join(FullPath(root, parentRelative), step.RenameTo!);
        var expectedTo = DataDirectoryPolicy.ExpectedFinalPath(journal.RootFinalPath, Path.Join(parentRelative, step.RenameTo!));
        try
        {
            using var obj = fs.Open(from, DataAccess.Rename);
            if (Mismatch(obj.State, step.Kind, step.Volume, step.FileId, DataDirectoryPolicy.ExpectedFinalPath(journal.RootFinalPath, step.Path)) is { } changed)
            {
                return changed;
            }
            obj.Rename(to);
            return null;
        }
        catch (DataAccessException) when (fs.DirectoryExists(to))
        {
            // Renamed before an interruption: accept only the very same folder under the new name.
            try
            {
                using var renamed = fs.Open(to, DataAccess.Inspect);
                return Mismatch(renamed.State, step.Kind, step.Volume, step.FileId, expectedTo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Р8: a new scan without violations, a protected root, and every file identical to the manifest.</summary>
    private List<string> Verify(string root, RepairJournal journal, string backup)
    {
        var failures = new List<string>();
        var scan = DataDirectoryScanner.Scan(fs, root, DataDirectoryPolicy.TrustedSids);
        failures.AddRange(scan.Violations.Select(v => "check: " + v));
        if (scan.Entries.Count > 0 && !DataDirectoryPolicy.IsRepaired(scan.Entries[0].State, isRoot: true))
        {
            failures.Add("check: the data folder's access list is not protected");
        }
        BackupManifest? manifest = null;
        try
        {
            var text = fs.ReadAllText(Path.Join(backup, ManifestName));
            if (text is null || !string.Equals(Fingerprint(text), journal.ManifestSha256, StringComparison.Ordinal))
            {
                failures.Add("the backup manifest is missing or changed");
            }
            else
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(text, Json);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            failures.Add("the backup manifest cannot be read: " + ex.Message);
        }
        if (manifest is null)
        {
            return failures;
        }
        var rename = journal.Steps.FirstOrDefault(s => s.Action == RepairStep.RenamePlugins && s.Status == Done);
        var files = scan.Entries.Where(e => e.State.Kind == DataObjectKind.File).ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries.Where(e => e.Kind == DataObjectKind.File))
        {
            if (!DataDirectoryPolicy.IsSafeRelativePath(entry.Path))
            {
                failures.Add("the backup manifest contains an invalid path");
                continue;
            }
            var relative = MapRenamed(entry.Path, rename);
            if (!files.Remove(relative, out var now))
            {
                failures.Add($"{Display(relative)}: missing after the repair");
                continue;
            }
            try
            {
                using var obj = fs.Open(now.State.FullPath, DataAccess.Read);
                using var content = obj.OpenContent();
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), entry.Sha256, StringComparison.Ordinal))
                {
                    failures.Add($"{Display(relative)}: content differs from the backup manifest");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Display(relative)}: {ex.Message}");
            }
        }
        failures.AddRange(files.Keys.Select(extra => $"{Display(extra)}: appeared during the repair"));
        return failures;
    }

    private static string MapRenamed(string relative, RepairStep? rename)
    {
        if (rename?.RenameTo is not { } to)
        {
            return relative;
        }
        var renamedTo = Path.Join(Path.GetDirectoryName(rename.Path)!, to);
        if (string.Equals(relative, rename.Path, StringComparison.OrdinalIgnoreCase))
        {
            return renamedTo;
        }
        var prefix = rename.Path + Path.DirectorySeparatorChar;
        return relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? Path.Join(renamedTo, relative[prefix.Length..]) : relative;
    }

    /// <summary>Null when the open object is the one planned: same identity, type, single link and resolved path.</summary>
    private static string? Mismatch(DataObjectState now, DataObjectKind kind, ulong volume, string fileId, string expectedFinalPath)
    {
        if (now.Kind != kind)
        {
            return $"it is now a {now.Kind}, was a {kind}";
        }
        if (now.Id.Volume != volume || !string.Equals(now.Id.FileId, fileId, StringComparison.Ordinal))
        {
            return "it was replaced by another object";
        }
        if (now.Kind == DataObjectKind.File && now.LinkCount != 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"it now has {now.LinkCount} hard links");
        }
        if (!DataDirectoryPolicy.SamePath(now.FinalPath, expectedFinalPath))
        {
            return $"it now resolves to {now.FinalPath ?? "(unknown)"}";
        }
        return null;
    }

    // ---------------------------------------------------------------- journal

    private void Save(RepairJournal journal, string backup)
    {
        journal.UpdatedAt = time.GetUtcNow();
        fs.WriteAllTextDurably(Path.Join(backup, JournalName), JsonSerializer.Serialize(journal, Json));
    }

    private bool TrySave(RepairJournal journal, string backup)
    {
        try
        {
            Save(journal, backup);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.WriteLine("The repair journal could not be updated: " + ex.Message);
            return false;
        }
    }

    private RepairJournal? TryLoadJournal(string backup, out string? error)
    {
        error = null;
        try
        {
            var text = fs.ReadAllText(Path.Join(backup, JournalName));
            return text is null ? null : JsonSerializer.Deserialize<RepairJournal>(text, Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "journal unreadable: " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// A journal is used only from a trusted backup next to this very data folder: the folder must still
    /// resolve where it did, every path in it must be relative, and the only rename allowed is the plugins
    /// folder to plugins.disabled-&lt;date-time&gt;.
    /// </summary>
    private RepairJournal? LoadForResume(string root, string backup)
    {
        var expectedBackupPrefix = Path.TrimEndingDirectorySeparator(root) + BackupInfix;
        if (!Path.TrimEndingDirectorySeparator(backup).StartsWith(expectedBackupPrefix, StringComparison.OrdinalIgnoreCase)
            || backup.EndsWith(IncompleteSuffix, StringComparison.OrdinalIgnoreCase)
            || !fs.DirectoryExists(backup))
        {
            console.WriteLine($"{backup} is not a backup of {root}. Nothing was changed.");
            return null;
        }
        if (BackupDistrust(backup) is { } distrust)
        {
            console.WriteLine($"{backup}: {distrust}. Nothing was changed.");
            return null;
        }
        var journal = TryLoadJournal(backup, out var error);
        if (journal is null)
        {
            console.WriteLine($"No repair journal in {backup}{(error is null ? "" : ": " + error)}. Nothing was changed.");
            return null;
        }
        if (!DataDirectoryPolicy.SamePath(Path.TrimEndingDirectorySeparator(journal.Root), Path.TrimEndingDirectorySeparator(root))
            || !DataDirectoryPolicy.SamePath(Path.TrimEndingDirectorySeparator(journal.Backup), Path.TrimEndingDirectorySeparator(backup))
            || !DataDirectoryPolicy.SamePath(journal.RootFinalPath, fs.ExpectedRootFinalPath(root)))
        {
            console.WriteLine($"The journal in {backup} belongs to another folder ({journal.Root}). Nothing was changed.");
            return null;
        }
        foreach (var step in journal.Steps)
        {
            var valid = DataDirectoryPolicy.IsSafeRelativePath(step.Path) && step.Action switch
            {
                RepairStep.ProtectRoot => step.Path.Length == 0 && step.RenameTo is null,
                RepairStep.Inherit => step.Path.Length > 0 && step.RenameTo is null,
                RepairStep.RenamePlugins => DataDirectoryPolicy.IsPluginsDirectory(step.Path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))
                    && DataDirectoryPolicy.IsQuarantineName(step.RenameTo),
                _ => false,
            };
            if (!valid)
            {
                console.WriteLine($"The journal in {backup} contains an invalid step ({step.Index.ToString(CultureInfo.InvariantCulture)}). Nothing was changed.");
                return null;
            }
        }
        return journal;
    }

    private OriginalSecurity? LoadOriginal(string root, string backup, RepairJournal journal)
    {
        try
        {
            var text = fs.ReadAllText(Path.Join(backup, OriginalSecurityName));
            if (text is null || !string.Equals(Fingerprint(text), journal.OriginalSecuritySha256, StringComparison.Ordinal))
            {
                console.WriteLine($"The saved original access in {backup} is missing or changed. Nothing was changed.");
                return null;
            }
            var original = JsonSerializer.Deserialize<OriginalSecurity>(text, Json);
            if (original is null
                || !DataDirectoryPolicy.SamePath(original.RootFinalPath, journal.RootFinalPath)
                || !DataDirectoryPolicy.SamePath(Path.TrimEndingDirectorySeparator(original.Root), Path.TrimEndingDirectorySeparator(root))
                || original.Entries.Any(e => !DataDirectoryPolicy.IsSafeRelativePath(e.Path) || string.IsNullOrEmpty(e.Sddl)))
            {
                console.WriteLine($"The saved original access in {backup} does not belong to this folder or contains invalid paths. Nothing was changed.");
                return null;
            }
            return original;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            console.WriteLine($"The saved original access in {backup} cannot be read: {ex.Message}. Nothing was changed.");
            return null;
        }
    }

    // ---------------------------------------------------------------- reports

    private void Report(DataScan scan)
    {
        console.WriteLine($"Data folder: {scan.Root}");
        if (scan.Violations.Count == 0)
        {
            return;
        }
        console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{scan.Violations.Count} problem(s):"));
        foreach (var violation in scan.Violations)
        {
            console.WriteLine("  " + violation);
        }
    }

    private void ReportUnfinished(IReadOnlyList<UnfinishedRepair> unfinished)
    {
        foreach (var repair in unfinished)
        {
            // Folder names next to the data folder can be chosen by anyone who may create folders there.
            var name = DataText.Clean(repair.Backup, 400);
            if (repair.Foreign)
            {
                console.WriteLine($"Ignored {name}: {DataText.Clean(repair.Detail, 400)}");
                continue;
            }
            console.WriteLine($"Unfinished repair ({DataText.Clean(repair.State)}): {name}{(repair.Detail.Length > 0 ? " — " + DataText.Clean(repair.Detail, 400) : "")}");
            console.WriteLine($"  Continue: datadir repair --continue \"{name}\"");
            console.WriteLine($"  Undo:     datadir restore-acl \"{name}\"");
        }
    }

    /// <summary>Р3: devices, tray path, certificate, plugins and files the service does not create.</summary>
    private void ShowContents(DataScan scan, Dictionary<string, string> previews)
    {
        console.WriteLine("");
        console.WriteLine("Fixing access does not make the contents trustworthy. Someone who could change this folder could also have added a device or changed settings:");
        var devices = previews.GetValueOrDefault("devices.json");
        if (devices is null)
        {
            console.WriteLine("  Paired devices: none (no devices.json)");
        }
        else
        {
            console.WriteLine("  Paired devices (devices.json):");
            foreach (var line in DescribeDevices(devices))
            {
                console.WriteLine("    " + line);
            }
        }
        var config = previews.GetValueOrDefault("config.json");
        foreach (var line in DescribeConfig(config))
        {
            console.WriteLine("  " + line);
        }
        if (scan.Plugins is { } plugins)
        {
            var inside = scan.Entries.Count(e => e.RelativePath.StartsWith(plugins.RelativePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  OpenRGB plugins folder: {inside} item(s); it will be renamed to plugins.disabled-<date-time> (quarantine), nothing is deleted"));
        }
        else
        {
            console.WriteLine("  OpenRGB plugins: none");
        }
        var unknown = UnknownObjects(scan);
        console.WriteLine(unknown.Count == 0 ? "  Files Nexus PC does not create: none" : "  Files Nexus PC does not create:");
        foreach (var path in unknown)
        {
            console.WriteLine("    " + Display(path));
        }
        console.WriteLine("After the repair check the devices in the tray (\"Устройства и разрешения\") and remove any you do not recognise.");
        console.WriteLine("");
    }

    internal static IReadOnlyList<string> UnknownObjects(DataScan scan)
    {
        var found = new List<string>();
        foreach (var entry in scan.Plan.Where(e => e.RelativePath.Length > 0))
        {
            var topLevel = !entry.RelativePath.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var code = entry.State.Kind == DataObjectKind.File && CodeExtensions.Any(x => entry.RelativePath.EndsWith(x, StringComparison.OrdinalIgnoreCase));
            if (code || (topLevel && !KnownTopLevel.Contains(entry.RelativePath, StringComparer.OrdinalIgnoreCase)))
            {
                found.Add(entry.RelativePath);
            }
        }
        return found;
    }

    internal static IReadOnlyList<string> DescribeDevices(string json)
    {
        if (json.StartsWith('!'))
        {
            return ["cannot be shown: " + json[1..]];
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ["not a device list"];
            }
            var lines = new List<string>();
            foreach (var device in document.RootElement.EnumerateArray().Take(100))
            {
                var name = Clean(Property(device, "Name")?.ToString() ?? "?");
                var paired = Clean(Property(device, "PairedAt")?.ToString() ?? "?");
                var permissions = Property(device, "Permissions") is { ValueKind: JsonValueKind.Array } p
                    ? string.Join(",", p.EnumerateArray().Select(x => Clean(x.ToString())))
                    : "?";
                var key = Property(device, "PublicKey")?.ToString() is { } b64 && TryBase64(b64) is { } bytes
                    ? Convert.ToHexStringLower(SHA256.HashData(bytes))[..16]
                    : "?";
                lines.Add($"\"{name}\" paired {paired}, permissions {(permissions.Length == 0 ? "none" : permissions)}, key {key}");
            }
            return lines.Count == 0 ? ["none"] : lines;
        }
        catch (JsonException)
        {
            return ["devices.json is not valid JSON"];
        }
    }

    internal static IReadOnlyList<string> DescribeConfig(string? json)
    {
        if (json is null)
        {
            return ["Settings: no config.json"];
        }
        if (json.StartsWith('!'))
        {
            return ["Settings (config.json) cannot be shown: " + json[1..]];
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var tray = Property(document.RootElement, "trayPath")?.ToString();
            var thumbprint = Property(document.RootElement, "certificateThumbprint")?.ToString();
            return
            [
                "Tray program allowed on the session channel (config.json trayPath): " + (tray is null ? "default (Program Files\\NexusPC\\Tray\\Nexus.Tray.exe)" : Clean(tray)),
                "Certificate (config.json certificateThumbprint): " + (thumbprint is null ? "not set" : Clean(thumbprint)),
            ];
        }
        catch (JsonException)
        {
            return ["config.json is not valid JSON"];
        }
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        return null;
    }

    private static byte[]? TryBase64(string text)
    {
        var buffer = new byte[(text.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(text, buffer, out var written) ? buffer[..written] : null;
    }

    /// <summary>Text from the untrusted folder, safe to print: no control characters, bounded length.</summary>
    private static string Clean(string text)
    {
        return DataText.Clean(text, 120);
    }

    private static string Display(string relative) => relative.Length == 0 ? "(data folder)" : DataText.Clean(relative);

    private static string FullPath(string root, string relative) => relative.Length == 0 ? root : Path.Join(root, relative);
}
