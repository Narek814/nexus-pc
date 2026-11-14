using System.Security.AccessControl;
using System.Security.Principal;

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

    /// <summary>
    /// Creates the data directory readable only by SYSTEM and Administrators (Windows) or the
    /// current user (elsewhere). Device keys and the audit log live here (SECURITY.md §3).
    /// </summary>
    public static void EnsureSecureDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

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
