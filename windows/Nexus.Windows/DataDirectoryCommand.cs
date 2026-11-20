using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Nexus.Core.Storage;

namespace Nexus.Windows;

/// <summary>
/// "datadir check | repair | repair --continue | restore-acl", shared by Nexus.Agent.exe and NexusPC-Setup.exe
/// (the installer needs it when there is no agent yet). Runs in the elevated console, never in the service:
/// repair needs the service stopped. This class only gathers Windows facts; every decision is in
/// Nexus.Core.Storage (DataDirectoryRepair, AgentPresencePolicy, InteractiveSessionPolicy).
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectoryCommand
{
    public const string ServiceName = "NexusPCAgent";

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DataDirectoryCommandLine.FolderName);

    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusPC");

    public static int Run(DataDirectoryCommandLine command, IRepairConsole console)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(console);
        // One datadir command, installation or removal at a time, for its whole run (protected lock, N1/N2).
        return ExclusiveRun.Run(new ProtectedDataDirectoryLock(), console.WriteLine, DataDirectoryRepair.ExitRefused, () => RunLocked(command, console));
    }

    private static int RunLocked(DataDirectoryCommandLine command, IRepairConsole console)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.DataDirectory ?? DefaultRoot));
            var localService = DataDirectoryRepair.IsLocalDataFolder(new DataDirectoryFileSystem(), root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultRoot)));
            var backup = command.Backup is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.Backup));
            if (command.Verb == "check")
            {
                var check = new DataDirectoryRepair(new DataDirectoryFileSystem(), console, TimeProvider.System);
                if (!IsElevated())
                {
                    console.WriteLine("Not elevated: run this in a command prompt opened with \"Run as administrator\", otherwise protected objects show as unreadable.");
                    return check.Check(root);
                }
                using var read = DataRepairEnvironment.EnablePrivileges(DataRepairEnvironment.ReadPrivileges);
                return check.Check(root);
            }

            using var privileges = IsElevated() ? DataRepairEnvironment.EnablePrivileges(DataRepairEnvironment.RepairPrivileges) : null;
            var environment = new WindowsRepairEnvironment(privileges, localService);
            if (!localService)
            {
                console.WriteLine("Not this PC's own data folder: the local Nexus PC service and its start type are not touched.");
            }
            var repair = new DataDirectoryRepair(new DataDirectoryFileSystem(), console, TimeProvider.System, environment);
            var failures = environment.PreconditionFailures();
            console.WriteLine("Note: one signed-in session lowers the risk that the folder changes during the repair; it does not remove it.");
            console.WriteLine("For a stricter guarantee use the offline variant (docs/SETUP.md, \"Восстановление папки данных\").");
            return command.Verb switch
            {
                "repair" => repair.Repair(root, failures),
                "continue" => repair.Continue(root, backup!, failures),
                "finalize" => repair.Finalize(root, backup!, failures),
                _ => repair.RestoreAcl(root, backup!, failures),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException)
        {
            // Everything after the first change is handled (and journaled) inside DataDirectoryRepair.
            console.WriteLine("Refused: " + DataText.Clean(ex.Message, 400) + " Nothing was changed.");
            return DataDirectoryRepair.ExitRefused;
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Preconditions, checked at the start and again before every change. The local service's start type is
    /// touched only when the folder is this PC's own data folder: with --data-dir pointing elsewhere (offline
    /// variant, a disk of another Windows) the local service has nothing to do with it.
    /// </summary>
    private sealed class WindowsRepairEnvironment(DataRepairEnvironment.PrivilegeScope? privileges, bool localService) : IRepairEnvironment
    {
        public IReadOnlyList<string> PreconditionFailures() => Preconditions(privileges);

        public string? ServiceStartType() => localService ? DataRepairEnvironment.ServiceStartType(ServiceName) : null;

        public void SetServiceStartType(string startType)
        {
            if (!localService)
            {
                throw new InvalidOperationException("not the local data folder: the local service is not touched");
            }
            DataRepairEnvironment.SetServiceStartType(ServiceName, startType);
        }
    }

    /// <summary>Elevated, privileges, service stopped, no agent anywhere, exactly one signed-in session (ours).</summary>
    private static List<string> Preconditions(DataRepairEnvironment.PrivilegeScope? privileges)
    {
        var failures = new List<string>();
        if (privileges is null)
        {
            failures.Add("Run this in a command prompt opened with \"Run as administrator\".");
            return failures;
        }
        if (privileges.Missing.Count > 0)
        {
            failures.Add("Missing privileges: " + string.Join(", ", privileges.Missing));
        }
        try
        {
            if (DataRepairEnvironment.ServiceState(ServiceName) is { } state and not 1)
            {
                failures.Add($"The Nexus PC service is not stopped (state {state}). Stop it first; this command never starts it.");
            }
        }
        catch (Win32Exception ex)
        {
            failures.Add("The state of the Nexus PC service cannot be read: " + ex.Message);
        }
        try
        {
            failures.AddRange(AgentPresencePolicy.Refusals(ExistingPipes(), Processes(), Environment.ProcessId, InstallRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            failures.Add("Running Nexus PC programs cannot be listed: " + ex.Message);
        }
        try
        {
            var refusal = InteractiveSessionPolicy.Refusal(DataRepairEnvironment.Sessions(), WindowsProcesses.SessionId(Environment.ProcessId));
            if (refusal is not null)
            {
                failures.Add("Sessions: " + DataText.Clean(refusal, 400) + ".");
            }
        }
        catch (Win32Exception ex)
        {
            failures.Add("The signed-in sessions cannot be listed: " + ex.Message);
        }
        return failures;
    }

    /// <summary>Which Nexus PC pipes exist at all (busy or closed to this account still counts), with their server process when it can be read.</summary>
    public static IReadOnlyList<ExistingPipe> ExistingPipes()
    {
        var found = new List<ExistingPipe>();
        foreach (var name in Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
            .Where(name => AgentPresencePolicy.PipeNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            var pid = DataRepairEnvironment.PipeServerProcessId(name);
            found.Add(new ExistingPipe(name, pid, pid is { } id ? WindowsProcesses.ImagePath(id) : null));
        }
        return found;
    }

    public static IReadOnlyList<RunningProcess> Processes()
    {
        var found = new List<RunningProcess>();
        foreach (var name in new[] { "Nexus.Agent", "OpenRGB" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    found.Add(new RunningProcess(process.Id, name, WindowsProcesses.ImagePath(process.Id)));
                }
            }
        }
        return found;
    }
}
