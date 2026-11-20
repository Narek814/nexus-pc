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
    /// <summary>An undone repair (acl-restored) whose job a later, completed repair took over.</summary>
    public const string Superseded = "superseded";

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

    /// <summary>Backups of earlier repairs undone by restore-acl that this repair finishes (marked superseded when it completes).</summary>
    public List<string> Supersedes { get; init; } = [];

    /// <summary>
    /// Finished states: nothing is left half done. acl-restored is not finished: the service stays disabled and
    /// blocked until a new repair or "datadir finalize" completes the procedure (owner decision, variant A).
    /// </summary>
    [JsonIgnore]
    public bool Finished => State is Completed or Superseded;
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
    /// Backups next to the data folder that need attention:
    /// <list type="bullet">
    /// <item>trusted ones with an unfinished journal (they block the service until finished);</item>
    /// <item>tampered ones: the root is owned by Administrators or SYSTEM (an ordinary user cannot set that) and
    /// holds a repair journal, but something inside no longer passes the check (an ACE added by Explorer's
    /// "Continue", a link, ...). Their journal is not trusted, so it cannot finish anything — and they block
    /// (fail closed) until an administrator deals with them;</item>
    /// <item>foreign ones: owned by anyone else. Reported after one look at the root, never walked, never used,
    /// and they never block;</item>
    /// <item>more than MaxBackupCandidates such folders owned by Administrators or SYSTEM: nothing can be decided safely, so the
    /// whole set blocks (fail closed) instead of possibly hiding a real unfinished repair.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<UnfinishedRepair> FindUnfinished(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<UnfinishedRepair>();
        var candidates = fs.Siblings(root, BackupInfix).Where(b => !b.EndsWith(IncompleteSuffix, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        // Pass 1: one look at each root. Folders an ordinary user owns are foreign: never walked, never counted.
        var adminOwned = new List<(string Backup, DataObjectState Root)>();
        foreach (var backup in candidates)
        {
            var (rootState, foreignDetail) = InspectBackupRoot(backup);
            if (rootState is null)
            {
                found.Add(new UnfinishedRepair(backup, "foreign", foreignDetail!, Foreign: true));
                continue;
            }
            adminOwned.Add((backup, rootState));
        }
        // Owner decision (variant B): only folders owned by Administrators or SYSTEM count towards the limit.
        // An ordinary user cannot create those, so this cannot be used to block the service or the installer.
        if (adminOwned.Count > MaxBackupCandidates)
        {
            found.Add(new UnfinishedRepair(Path.TrimEndingDirectorySeparator(root) + BackupInfix + "*", TooManyBackups,
                string.Create(CultureInfo.InvariantCulture, $"{adminOwned.Count} folders owned by Administrators or SYSTEM are named like backups (more than {MaxBackupCandidates}); remove the ones you do not recognise")));
            return found;
        }
        // Pass 2: only admin-owned roots are checked further (journal, then the whole folder).
        foreach (var (backup, rootState) in adminOwned)
        {
            var (trust, detail) = ClassifyAdminOwned(backup, rootState);
            if (trust == BackupTrust.Foreign)
            {
                found.Add(new UnfinishedRepair(backup, "foreign", detail, Foreign: true));
                continue;
            }
            if (trust == BackupTrust.Tampered)
            {
                found.Add(new UnfinishedRepair(backup, Tampered, detail));
                continue;
            }
            if (trust == BackupTrust.NoJournal)
            {
                continue; // stopped before the journal was written: the data folder was never changed
            }
            var journal = TryLoadJournal(backup, out var error);
            if (journal is null)
            {
                found.Add(new UnfinishedRepair(backup, Tampered, error ?? "the journal cannot be read"));
                continue;
            }
            if (!journal.Finished)
            {
                found.Add(new UnfinishedRepair(backup, journal.State, string.Join("; ", journal.Problems)));
            }
        }
        return found;
    }

    /// <summary>Admin-owned folders named like backups that are checked, at most (folders of ordinary users are not counted).</summary>
    public const int MaxBackupCandidates = 10_000;
    public const string Tampered = "tampered";
    public const string TooManyBackups = "too-many-backups";

    private enum BackupTrust
    {
        Foreign,
        NoJournal,
        Tampered,
        Trusted,
    }

    /// <summary>
    /// The root first, through one handle: anything not owned by Administrators or SYSTEM is foreign and is
    /// never walked (no cost for planted folders, whatever they contain). Only admin-owned roots with a
    /// journal are walked in full; any problem inside makes them tampered.
    /// </summary>
    private (BackupTrust Trust, string Detail) ClassifyBackup(string backup)
    {
        var (root, foreignDetail) = InspectBackupRoot(backup);
        return root is null ? (BackupTrust.Foreign, foreignDetail!) : ClassifyAdminOwned(backup, root);
    }

    /// <summary>One open of the root: its state when owned by Administrators or SYSTEM, otherwise why it is foreign.</summary>
    private (DataObjectState? Root, string? ForeignDetail) InspectBackupRoot(string backup)
    {
        try
        {
            using var handle = fs.Open(backup, DataAccess.Inspect);
            var root = handle.State;
            return root.OwnerSid is DataDirectoryPolicy.Administrators or DataDirectoryPolicy.LocalSystem
                ? (root, null)
                : (null, "foreign folder, not used: not created by the repair command (owner " + DataText.Clean(root.OwnerSid ?? "unknown") + ")");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "foreign folder, not used: " + ex.Message);
        }
    }

    /// <summary>
    /// An admin-owned root with a journal: walked in full, and any problem makes it tampered — whatever its
    /// journal says, "completed" included (the journal of a changed folder is not believed).
    /// </summary>
    private (BackupTrust Trust, string Detail) ClassifyAdminOwned(string backup, DataObjectState root)
    {
        try
        {
            using var journal = fs.Open(Path.Join(backup, JournalName), DataAccess.Inspect);
        }
        catch (DataAccessException ex) when (ex.NotFound)
        {
            return (BackupTrust.NoJournal, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (BackupTrust.Tampered, TamperedText + ex.Message);
        }
        if (root.Kind != DataObjectKind.Directory || !root.DaclProtected)
        {
            return (BackupTrust.Tampered, TamperedText + "its root is not a protected folder");
        }
        var scan = DataDirectoryScanner.Scan(fs, backup, DataDirectoryPolicy.TrustedSids);
        return scan.Violations.Count > 0
            ? (BackupTrust.Tampered, TamperedText + scan.Violations[0])
            : (BackupTrust.Trusted, "");
    }

    public const string TamperedText = "tampered backup (changed after the repair wrote it), its journal is not trusted and it blocks the service: ";

    /// <summary>Null when the backup is trusted (made by the repair command and unchanged); otherwise why not.</summary>
    private string? BackupDistrust(string backup)
    {
        var (trust, detail) = ClassifyBackup(backup);
        return trust switch
        {
            BackupTrust.Trusted => null,
            BackupTrust.NoJournal => "no repair journal in it",
            _ => detail,
        };
    }

    // ---------------------------------------------------------------- repair

    public int Repair(string root, IReadOnlyList<string> preconditionFailures)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(preconditionFailures);
        string? backup = null;
        List<(string Backup, RepairJournal Journal)> undone = [];
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
            // A repair undone by restore-acl may be taken over by this one (it keeps that repair's saved start
            // type and marks it superseded when it completes); any other unfinished repair must be finished first.
            if (backups.Any(b => !b.Foreign && b.State != RepairJournal.Restored))
            {
                console.WriteLine("Nothing was changed. Finish or undo the earlier repair first.");
                return ExitRefused;
            }
            undone = [.. backups.Where(b => !b.Foreign && b.State == RepairJournal.Restored)
                .Select(b => (b.Backup, Journal: TryLoadJournal(b.Backup, out _)))
                .Where(b => b.Journal is not null)
                .Select(b => (b.Backup, b.Journal!))];

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
                if (undone.Count > 0)
                {
                    // The folder is fine, but an undone repair still blocks the service: only finalize ends it.
                    console.WriteLine("Nothing to repair, but an earlier repair is not finished and the service stays blocked.");
                    foreach (var (older, _) in undone)
                    {
                        console.WriteLine($"  Finish it: datadir finalize \"{DataText.Clean(older, 400)}\"");
                    }
                    return ExitRefused;
                }
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
            plan = Plan(scan, root, rootFinal, backup, stamp, manifestSha, undone);
            console.WriteLine($"Original access saved: {Path.Join(backup, OriginalSecurityName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DataRepairException or JsonException or InvalidOperationException)
        {
            console.WriteLine("Repair refused: " + ex.Message);
            if (backup is not null && !MarkIncomplete(backup) && fs.ReadAllText(Path.Join(backup, JournalName)) is not null)
            {
                console.WriteLine($"The data folder was not changed, but the backup {backup} still holds an unfinished repair journal; the service refuses to start while it is there.");
                console.WriteLine($"End it with: datadir repair --continue \"{backup}\".");
                return ExitStopped;
            }
            console.WriteLine("The data folder was not changed.");
            return ExitRefused;
        }

        // Р7 and Р8.
        return Apply(root, plan.Journal, plan.Original, backup, fresh: true);
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
        if (journal.State is RepairJournal.Restored or RepairJournal.RestoreFailed or RepairJournal.Superseded)
        {
            console.WriteLine(journal.State switch
            {
                RepairJournal.Restored => $"The original access of this repair was restored. Finish the procedure with a new \"datadir repair\", or, if the folder was fixed otherwise, \"datadir finalize \\\"{backup}\\\"\". Nothing was changed.",
                RepairJournal.RestoreFailed => $"Restoring the original access did not finish; run \"datadir restore-acl \\\"{backup}\\\"\" again. Nothing was changed.",
                _ => "This repair was taken over by a later one. Nothing was changed.",
            });
            return ExitRefused;
        }
        return Apply(root, journal, original, backup, fresh: false);
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
        var incomplete = new List<string>();
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
                // What the object has now decides, not what was written (R10-1).
                switch (DataDirectoryPolicy.CompareRestored(entry.Sddl, obj.ReadSecurityDescriptor()))
                {
                    case RestoreOutcome.AutoInheritedLost:
                        incomplete.Add(Display(relative));
                        break;
                    case RestoreOutcome.Different:
                        problems.Add($"{Display(relative)}: the access after the restore differs from the saved one (or cannot be read)");
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Display(relative)}: {ex.Message}");
            }
        }
        // Variant A (owner): restore-acl does not finish the procedure. The start type stays as the repair left
        // it (disabled), the original one stays saved in the journal, and the service start check keeps
        // refusing until a new repair or "datadir finalize" passes every check.
        journal.State = problems.Count == 0 ? RepairJournal.Restored : RepairJournal.RestoreFailed;
        journal.Problems.AddRange(problems.Select(p => "restore-acl: " + p));
        journal.Problems.AddRange(incomplete.Select(p => "restore-acl: warning: " + p + ": restored except the inherited-entries flag (AI)"));
        var saved = TrySave(journal, backup);
        foreach (var problem in problems)
        {
            console.WriteLine("  " + problem);
        }
        if (problems.Count == 0 && saved && incomplete.Count > 0)
        {
            // Owner decision (R10-1): exit 0, state acl-restored, but never reported as an exact restore.
            console.WriteLine($"WARNING: the original access was restored INCOMPLETELY from {backup}: for {incomplete.Count} object(s) Windows did not keep the \"inherited entries\" flag (AI); owner, group and every access entry are as saved.");
            foreach (var path in incomplete)
            {
                console.WriteLine("  " + path);
            }
            console.WriteLine("Contents were not changed. The backup is kept. The service was not started.");
            console.WriteLine($"The repair is NOT finished: the service start type is now {CurrentStartType()} and the service refuses to start.");
            console.WriteLine($"To finish: run \"datadir repair\" again (it takes over this backup's saved start type), or fix the folder by hand and run \"datadir finalize \\\"{backup}\\\"\".");
            return ExitOk;
        }
        if (problems.Count == 0 && saved)
        {
            console.WriteLine($"Original access restored from {backup}. Contents were not changed. The backup is kept. The service was not started.");
            console.WriteLine($"The repair is NOT finished: the service start type is now {CurrentStartType()} and the service refuses to start.");
            console.WriteLine($"To finish: run \"datadir repair\" again (it takes over this backup's saved start type), or fix the folder by hand and run \"datadir finalize \\\"{backup}\\\"\".");
            return ExitOk;
        }
        console.WriteLine($"Original access was not restored for {problems.Count} object(s){(saved ? "" : " and the journal could not be updated")}. The backup is kept: {backup}.");
        console.WriteLine($"The service was not started; its start type is now {CurrentStartType()} and it refuses to start. Run restore-acl again.");
        return ExitStopped;
    }

    /// <summary>
    /// "datadir finalize &lt;backup&gt;": the administrator's explicit end of a repair procedure that did not
    /// finish by itself (after restore-acl, or when the start type could not be put back). Checks, in order:
    /// preconditions (again), the trusted backup and its journal, the data folder (strict scan: no links, no
    /// hard links, trusted owners and grantees only, protected root, every file as in the manifest), and the
    /// backup copy (every file as in the manifest). Only then, as the last step, the saved start type goes back,
    /// and only after that the repair is marked completed. Any failure leaves it unfinished: the service stays
    /// disabled and refuses to start.
    /// </summary>
    public int Finalize(string root, string backup, IReadOnlyList<string> preconditionFailures)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(preconditionFailures);
        RepairJournal? journal;
        try
        {
            if (Refuse(preconditionFailures) || (journal = LoadForResume(root, backup)) is null || LoadOriginal(root, backup, journal) is null)
            {
                return ExitRefused;
            }
            if (journal.State == RepairJournal.Completed)
            {
                console.WriteLine("This repair is already completed.");
                return ExitOk;
            }
            if (journal.State == RepairJournal.Superseded)
            {
                console.WriteLine("This repair was taken over by a later one; nothing to finalize.");
                return ExitOk;
            }
            if (Refuse(_environment.PreconditionFailures()))
            {
                return ExitRefused;
            }
            // Another unfinished repair (not one this backup took over) keeps the procedure open: finishing this
            // one would put the start type back while the other is still half done.
            if (OtherUnfinished(root, backup, journal) is { Count: > 0 } others)
            {
                foreach (var other in others)
                {
                    console.WriteLine($"  Unfinished: {DataText.Clean(other.Backup, 400)} ({DataText.Clean(other.State)}) — {FinishCommand(other)}");
                }
                console.WriteLine($"Not finalized: another repair is not finished; finish it first. Nothing was changed; the service start type is {CurrentStartType()} and the service refuses to start.");
                return ExitRefused;
            }
            var failures = Verify(root, journal, backup);
            failures.AddRange(VerifyBackup(backup, journal));
            if (failures.Count > 0)
            {
                foreach (var failure in failures)
                {
                    console.WriteLine("  " + failure);
                }
                console.WriteLine($"Not finalized: the checks above failed. Nothing was changed; the service start type is {CurrentStartType()} and the service refuses to start.");
                console.WriteLine($"Fix the folder (for example with a new \"datadir repair\") and run \"datadir finalize \\\"{backup}\\\"\" again.");
                return ExitRefused;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            console.WriteLine("Not finalized: " + ex.Message + $" Nothing was changed; the service refuses to start.");
            return ExitRefused;
        }
        try
        {
            Complete(root, journal, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            console.WriteLine("Not finalized: " + ex.Message);
            console.WriteLine($"The repair stays unfinished; the service start type is {CurrentStartType()} and the service refuses to start. Run \"datadir finalize \\\"{backup}\\\"\" again.");
            return ExitStopped;
        }
        console.WriteLine("Repair finalized: the folder, its access and the backup passed every check. The backup is kept: " + backup);
        console.WriteLine("Check the paired devices in the tray (\"Устройства и разрешения\") and remove any you do not recognise, then start the service.");
        return ExitOk;
    }

    /// <summary>Every file of the backup copy still matches the manifest (read through handles, not following links).</summary>
    private List<string> VerifyBackup(string backup, RepairJournal journal)
    {
        var failures = new List<string>();
        var text = fs.ReadAllText(Path.Join(backup, ManifestName));
        if (text is null || !string.Equals(Fingerprint(text), journal.ManifestSha256, StringComparison.Ordinal)
            || JsonSerializer.Deserialize<BackupManifest>(text, Json) is not { } manifest)
        {
            failures.Add("backup: the manifest is missing or changed");
            return failures;
        }
        var data = Path.Join(backup, DataFolder);
        var backupFinal = fs.ExpectedRootFinalPath(backup);
        foreach (var entry in manifest.Entries)
        {
            if (!DataDirectoryPolicy.IsSafeRelativePath(entry.Path) || entry.Path.Length == 0)
            {
                failures.Add("backup: the manifest contains an invalid path");
                continue;
            }
            try
            {
                using var obj = fs.Open(Path.Join(data, entry.Path), DataAccess.Read);
                var expectedPath = DataDirectoryPolicy.ExpectedFinalPath(backupFinal, Path.Join(DataFolder, entry.Path));
                if (obj.State.Kind != entry.Kind || (entry.Kind == DataObjectKind.File && obj.State.LinkCount != 1) || !DataDirectoryPolicy.SamePath(obj.State.FinalPath, expectedPath))
                {
                    failures.Add($"backup: {Display(entry.Path)} is not the copy that was made");
                    continue;
                }
                if (entry.Kind == DataObjectKind.File)
                {
                    using var content = obj.OpenContent();
                    if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), entry.Sha256, StringComparison.Ordinal))
                    {
                        failures.Add($"backup: {Display(entry.Path)} differs from the manifest");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"backup: {Display(entry.Path)}: {ex.Message}");
            }
        }
        return failures;
    }

    /// <summary>
    /// What the service start check refuses on, in this order: first an unfinished repair (it names the exact
    /// command that finishes it), only then the folder's own problems.
    /// </summary>
    public static (IReadOnlyList<UnfinishedRepair> Unfinished, IReadOnlyList<DataViolation> Violations) StartCheck(IDataFileSystem fs, string root, IReadOnlySet<string> trusted)
    {
        ArgumentNullException.ThrowIfNull(fs);
        var unfinished = new DataDirectoryRepair(fs, new SilentConsole(), TimeProvider.System).FindUnfinished(root).Where(r => !r.Foreign).ToList();
        if (unfinished.Count > 0)
        {
            return (unfinished, []);
        }
        return ([], DataDirectoryScanner.Scan(fs, root, trusted).Violations);
    }

    /// <summary>
    /// Whether <paramref name="root"/> is this PC's own data folder, by where both resolve (8.3 names,
    /// "C:\Users\All Users", \\?\ forms all count as the same). If that cannot be told, it counts as local:
    /// then the local service is disabled for the repair, which is the safe side.
    /// </summary>
    public static bool IsLocalDataFolder(IDataFileSystem fs, string root, string defaultRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);
        try
        {
            return DataDirectoryPolicy.SamePath(fs.ExpectedRootFinalPath(root), fs.ExpectedRootFinalPath(defaultRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>The command that finishes an unfinished repair in this state.</summary>
    public static string FinishCommand(UnfinishedRepair repair)
    {
        ArgumentNullException.ThrowIfNull(repair);
        var backup = DataText.Clean(repair.Backup, 400);
        return repair.State switch
        {
            RepairJournal.Restored => $"datadir repair (a new repair takes over), or after fixing the folder by hand: datadir finalize \"{backup}\"",
            RepairJournal.RestoreFailed => $"datadir restore-acl \"{backup}\" (again), then datadir repair",
            Tampered => $"no command can trust this backup. Inspect \"{backup}\" by hand; to release the block, rename it (as administrator) so that its name no longer starts with \"{DataText.Clean(Path.GetFileName(backup.Split(BackupInfix)[0]))}{BackupInfix}\", then run datadir check (see docs/SETUP.md)",
            TooManyBackups => "remove the folders named like backups that you do not recognise, then run datadir check",
            _ => $"datadir repair --continue \"{backup}\" (or undo: datadir restore-acl \"{backup}\")",
        };
    }

    private sealed class SilentConsole : IRepairConsole
    {
        public void WriteLine(string text)
        {
        }

        public string? ReadLine() => null;
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

    /// <summary>Unfinished trusted repairs other than this backup and the ones it took over.</summary>
    private List<UnfinishedRepair> OtherUnfinished(string root, string backup, RepairJournal journal) =>
        [.. FindUnfinished(root).Where(r => !r.Foreign
            && !DataDirectoryPolicy.SamePath(Path.TrimEndingDirectorySeparator(r.Backup), Path.TrimEndingDirectorySeparator(backup))
            && !journal.Supersedes.Any(s => DataDirectoryPolicy.SamePath(Path.TrimEndingDirectorySeparator(s), Path.TrimEndingDirectorySeparator(r.Backup))))];

    /// <summary>True when the backup now ends in .incomplete (it no longer counts as a repair).</summary>
    private bool MarkIncomplete(string backup)
    {
        try
        {
            fs.MoveDirectory(backup, backup + IncompleteSuffix);
            console.WriteLine($"The partial backup is kept as {backup + IncompleteSuffix}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.WriteLine($"The partial backup {backup} could not be renamed ({ex.Message}); it is kept as is.");
            return false;
        }
    }

    /// <summary>Р6: saves the original security of every planned object, then the journal with the steps.</summary>
    private (RepairJournal, OriginalSecurity) Plan(DataScan scan, string root, string rootFinal, string backup, string stamp, string manifestSha, List<(string Backup, RepairJournal Journal)> undone)
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
            // Saved before the service is disabled; put back only after a verified repair or "datadir finalize".
            // Taking over an undone repair: the service is already disabled by it, its saved start type is the real one.
            OriginalStartType = undone.Select(u => u.Journal).FirstOrDefault(j => j.ServiceDisabled && !j.ServiceRestored)?.OriginalStartType
                ?? _environment.ServiceStartType(),
            Supersedes = [.. undone.Select(u => u.Backup)],
        };
        Save(journal, backup);
        return (journal, parsed);
    }

    /// <summary>Р7 (owner and DACL by handle, after checking each object again) and Р8 (verification).</summary>
    private int Apply(string root, RepairJournal journal, OriginalSecurity original, string backup, bool fresh)
    {
        var saved = original.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        try
        {
            journal.State = RepairJournal.InProgress;
            Save(journal, backup);
            // No automatic start in an intermediate state: the service is disabled before the first change of access.
            if (journal.OriginalStartType is not null && journal.OriginalStartType != DisabledStartType && !journal.ServiceDisabled)
            {
                try
                {
                    _environment.SetServiceStartType(DisabledStartType);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return DisableFailed(journal, backup, fresh, ex);
                }
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
            Complete(root, journal, backup); // only now: access fixed and verified
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            console.WriteLine("Stopped: " + ex.Message);
            StoppedAdvice(backup);
            return ExitStopped;
        }
        console.WriteLine("Repair completed: access fixed, every file matches the backup manifest. The backup is kept: " + backup);
        console.WriteLine("Check the paired devices in the tray (\"Устройства и разрешения\") and remove any you do not recognise, then start the service.");
        return ExitOk;
    }

    /// <summary>
    /// The service could not be disabled: nothing is said about it that is not true. A fresh repair changed no
    /// access yet, so it is abandoned: the backup is renamed .incomplete and then nothing blocks the service.
    /// If even that rename fails, the backup keeps its stopped journal and blocks the service start; that is
    /// said plainly, with the command that ends it (code 3). A continued repair stays unfinished (the service
    /// start check keeps refusing) and reports the real start type.
    /// </summary>
    private int DisableFailed(RepairJournal journal, string backup, bool fresh, Exception ex)
    {
        console.WriteLine("The Nexus PC service could not be disabled: " + ex.Message);
        console.WriteLine("Service start type now: " + CurrentStartType() + ". No access in the data folder was changed by this run.");
        if (fresh)
        {
            journal.State = RepairJournal.Stopped;
            journal.Problems.Add("the service could not be disabled; repair abandoned before any change");
            TrySave(journal, backup);
            if (MarkIncomplete(backup))
            {
                console.WriteLine("Repair refused. The data folder was not changed.");
                return ExitRefused;
            }
            console.WriteLine($"Repair refused. The data folder was not changed, but the backup {backup} still holds an unfinished repair journal:");
            console.WriteLine("the Nexus PC service refuses to start while it is there.");
            console.WriteLine($"End it with: datadir repair --continue \"{backup}\" (it disables the service and finishes the repair).");
            return ExitStopped;
        }
        journal.State = RepairJournal.Stopped;
        journal.Problems.Add("the service could not be disabled");
        TrySave(journal, backup);
        console.WriteLine($"The repair stays unfinished; the service refuses to start until it is finished. Try again: datadir repair --continue \"{backup}\"");
        return ExitStopped;
    }

    /// <summary>
    /// The last steps, in this order: no other unfinished repair may be left (it would keep a half-done state
    /// behind a "completed" mark), earlier undone repairs marked superseded, preconditions checked once more,
    /// the saved start type put back (only after every check passed), this one marked completed. Any failure
    /// throws and leaves the journal unfinished, so the service start check keeps refusing — there is no
    /// window in which the service could start half done.
    /// </summary>
    private void Complete(string root, RepairJournal journal, string backup)
    {
        if (OtherUnfinished(root, backup, journal) is { Count: > 0 } others)
        {
            throw new InvalidOperationException("another repair is not finished: " + string.Join(", ", others.Select(o => DataText.Clean(o.Backup, 400))));
        }
        // Marking older journals and putting the start type back are changes too: the preconditions are checked
        // once more before the first of them.
        if (_environment.PreconditionFailures() is { Count: > 0 } failures)
        {
            throw new InvalidOperationException("preconditions no longer hold: " + string.Join("; ", failures));
        }
        foreach (var older in journal.Supersedes)
        {
            if (!Path.TrimEndingDirectorySeparator(older).StartsWith(Path.TrimEndingDirectorySeparator(root) + BackupInfix, StringComparison.OrdinalIgnoreCase)
                || BackupDistrust(older) is not null)
            {
                continue; // never write into a folder that is not a trusted backup of this data folder
            }
            if (TryLoadJournal(older, out _) is { State: RepairJournal.Restored } undone)
            {
                undone.State = RepairJournal.Superseded;
                undone.Problems.Add("superseded by " + backup);
                Save(undone, older);
            }
        }
        RestoreStartType(journal, backup); // the last step before the completion mark; if it fails, no mark
        journal.State = RepairJournal.Completed;
        Save(journal, backup);
        if (journal.ServiceRestored)
        {
            console.WriteLine($"The service start type is back to \"{journal.OriginalStartType}\".");
        }
    }

    private void RestoreStartType(RepairJournal journal, string backup)
    {
        if (!journal.ServiceDisabled || journal.ServiceRestored || journal.OriginalStartType is null)
        {
            return;
        }
        if (_environment.ServiceStartType() is null)
        {
            // The service was removed meanwhile (--uninstall from an older installer, by hand): there is no start
            // type to put back and nothing that could start. Every check has already passed.
            console.WriteLine("The Nexus PC service is not installed any more; there is no start type to put back.");
            journal.ServiceRestored = true;
            Save(journal, backup);
            return;
        }
        _environment.SetServiceStartType(journal.OriginalStartType);
        journal.ServiceRestored = true;
        Save(journal, backup);
    }

    private string CurrentStartType()
    {
        try
        {
            return _environment.ServiceStartType() ?? "not installed";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "unknown (" + ex.Message + ")";
        }
    }

    private void StoppedAdvice(string backup)
    {
        console.WriteLine($"Nothing else was changed. The backup and the journal are kept: {backup}.");
        console.WriteLine($"The service was not started; its start type is now: {CurrentStartType()}. It refuses to start while this repair is unfinished.");
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
            if (repair.State is RepairJournal.Restored or RepairJournal.RestoreFailed or Tampered or TooManyBackups)
            {
                console.WriteLine("  Finish:   " + FinishCommand(repair));
                continue;
            }
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
