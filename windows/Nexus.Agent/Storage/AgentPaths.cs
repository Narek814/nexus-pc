using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Nexus.Core.Storage;
using Nexus.Windows;

namespace Nexus.Agent.Storage;

internal static class AgentPaths
{
    /// <summary>%ProgramData%\NexusPC on Windows; NEXUS_DATA_DIR or ~/.local/share/NexusPC elsewhere (development only).</summary>
    public static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexusPC");
        }
        return Environment.GetEnvironmentVariable("NEXUS_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPC");
    }

    public const string EventLogSource = "Nexus.Agent";
    public const int UntrustedDataEventId = 1001;

    /// <summary>
    /// Creates the data directory readable only by SYSTEM and Administrators (Windows) or the
    /// current user (elsewhere). Device keys and the audit log live here (SECURITY.md §3).
    /// On Windows a missing folder is created with its protected descriptor in place from the first
    /// moment; an existing one is never changed, only checked (owner, access entries, links, hard
    /// links, resolved paths, OpenRGB plugins). Any problem stops the service: the problems go to the
    /// Windows event log and nothing is modified (repair: "Nexus.Agent datadir repair", elevated).
    /// </summary>
    /// <exception cref="DataDirectoryUntrustedException">The existing folder fails the check (Windows).</exception>
    public static void EnsureSecureDirectory(string path) => EnsureSecureDirectory(path, TestTrustedSids);

    /// <summary>
    /// Tests only, set in test code (never from configuration, environment or command line): the subjects
    /// the start-up check trusts instead of SYSTEM/Administrators/TrustedInstaller, and permission to create
    /// a test folder owned by the test account when it may not name Administrators as owner. Null in production.
    /// </summary>
    internal static IReadOnlySet<string>? TestTrustedSids { get; set; }

    /// <summary>Production: exactly SYSTEM, Administrators, TrustedInstaller.</summary>
    public static IReadOnlySet<string> TrustedSids(IReadOnlySet<string>? testOverride) => testOverride ?? DataDirectoryPolicy.TrustedSids;

    internal static void EnsureSecureDirectory(string path, IReadOnlySet<string>? testOverride)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateProtectedIfMissing(path, allowTestOwner: testOverride is not null);
            // First an unfinished repair (its journal lives in a trusted backup, so it survives a reboot): it names
            // the command that finishes it. Only then the folder's own problems.
            var (unfinished, violations) = DataDirectoryRepair.StartCheck(new DataDirectoryFileSystem(),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), TrustedSids(testOverride));
            if (unfinished.Count > 0)
            {
                var report = UnfinishedReport(path, unfinished);
                WriteEventLog(report);
                throw new DataDirectoryUntrustedException(report);
            }
            if (violations.Count > 0)
            {
                var report = UntrustedReport(path, violations);
                WriteEventLog(report);
                throw new DataDirectoryUntrustedException(report);
            }
        }
        else
        {
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Creates the folder with owner Administrators and a protected SYSTEM/Administrators DACL, unless it exists.</summary>
    [SupportedOSPlatform("windows")]
    public static void CreateProtectedIfMissing(string path, bool allowTestOwner = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetDirectoryName(full) is { } parent)
        {
            Directory.CreateDirectory(parent); // C:\ProgramData always exists; development and test paths may not
        }
        var error = DataDirectoryFileSystem.TryCreateProtectedDirectory(full, withOwner: true);
        var programData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        if (error == DataDirectoryFileSystem.ErrorInvalidOwner && allowTestOwner
            && !full.StartsWith(programData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // Test mode only, never under ProgramData: a test run that may not name Administrators as owner.
            error = DataDirectoryFileSystem.TryCreateProtectedDirectory(full, withOwner: false);
        }
        if (error != 0 && !DataDirectoryFileSystem.IsAlreadyExists(error))
        {
            throw new IOException($"Cannot create {full}: {new System.ComponentModel.Win32Exception(error).Message}");
        }
    }

    /// <summary>The full handle-based walk (Nexus.Core.Storage.DataDirectoryScanner); changes nothing.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<DataViolation> CheckDirectory(string path, IReadOnlySet<string> trusted) =>
        DataDirectoryScanner.Scan(new DataDirectoryFileSystem(), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), trusted).Violations;

    /// <summary>Unfinished repairs in trusted backups next to the folder (foreign folders are ignored: they cannot block).</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<UnfinishedRepair> UnfinishedRepairs(string path) =>
        [.. new DataDirectoryRepair(new DataDirectoryFileSystem(), new SilentConsole(), TimeProvider.System)
            .FindUnfinished(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Where(r => !r.Foreign)];

    public static string UnfinishedReport(string path, IReadOnlyList<UnfinishedRepair> unfinished)
    {
        ArgumentNullException.ThrowIfNull(unfinished);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Nexus PC did not start: a repair of the data folder {path} is not finished. Nothing was changed.");
        foreach (var repair in unfinished)
        {
            var backup = DataText.Clean(repair.Backup, 400);
            report.AppendLine(CultureInfo.InvariantCulture, $"  {backup} ({DataText.Clean(repair.State)})");
            report.AppendLine(CultureInfo.InvariantCulture, $"  To finish: Nexus.Agent.exe {DataDirectoryRepair.FinishCommand(repair)}");
        }
        report.AppendLine("Run it in a command prompt opened as administrator, with the service stopped. The service starts again only after the repair is completed and verified (the saved start type is put back then).");
        return report.ToString();
    }

    private sealed class SilentConsole : IRepairConsole
    {
        public void WriteLine(string text)
        {
        }

        public string? ReadLine() => null;
    }

    public static string UntrustedReport(string path, IReadOnlyList<DataViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Nexus PC did not start: the data folder {path} is not protected as expected. Nothing was changed.");
        report.AppendLine("Problems (path: kind — owner, access entry, link count or type):");
        foreach (var violation in violations.Take(200))
        {
            report.AppendLine("  " + violation);
        }
        if (violations.Count > 200)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  … and {violations.Count - 200} more");
        }
        report.AppendLine("Details: \"Nexus.Agent.exe datadir check\". Repair (backup first): \"Nexus.Agent.exe datadir repair\" in a command prompt run as administrator, with the service stopped.");
        return report.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static void WriteEventLog(string report)
    {
        try
        {
            if (!EventLog.SourceExists(EventLogSource))
            {
                EventLog.CreateEventSource(EventLogSource, "Application");
            }
            EventLog.WriteEntry(EventLogSource, report.Length <= 30_000 ? report : report[..30_000], EventLogEntryType.Error, UntrustedDataEventId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Console.Error.WriteLine(report); // not elevated (development): the console still shows it
        }
    }
}

/// <summary>The data folder failed the start-up check; the service does not start.</summary>
internal sealed class DataDirectoryUntrustedException(string message) : InvalidOperationException(message);

internal static class AtomicFile
{
    /// <summary>Write to a temp file, then rename over the target, so a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
