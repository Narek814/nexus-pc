using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Nexus.Core.Storage;
using Nexus.Windows;

namespace Nexus.Setup;

/// <summary>The transaction's file layer on NTFS: renames through MoveFileEx (same volume, no copy), journal written through.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInstallFileSystem : IInstallFileSystem
{
    private const int MaxJournalBytes = 1024 * 1024;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <summary>B-3: opened by handle with FILE_FLAG_OPEN_REPARSE_POINT; links and untrusted owners refuse.</summary>
    public void CheckObject(string path)
    {
        if (!Path.Exists(path) && !IsDanglingLink(path))
        {
            return;
        }
        try
        {
            using var open = new DataDirectoryFileSystem().Open(path, DataAccess.Inspect);
            if (open.State.Kind == DataObjectKind.ReparsePoint || (open.State.Attributes & 0x400) != 0)
            {
                throw new UnsafeInstallObjectException("reparse point: " + path);
            }
            if (open.State.OwnerSid is null || !DataDirectoryPolicy.TrustedSids.Contains(open.State.OwnerSid))
            {
                throw new UnsafeInstallObjectException($"untrusted owner {open.State.OwnerSid ?? "(none)"}: {path}");
            }
        }
        catch (DataAccessException e) when (!e.NotFound)
        {
            throw new UnsafeInstallObjectException(e.Message);
        }
    }

    private static bool IsDanglingLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void EnsureStaging(string path)
    {
        if (!Directory.Exists(path) && !IsDanglingLink(path))
        {
            var error = DataDirectoryFileSystem.TryCreateProtectedDirectory(path, withOwner: true);
            if (error != 0)
            {
                throw new IOException($"cannot create {path} (error {error})");
            }
        }
        using var open = new DataDirectoryFileSystem().Open(path, DataAccess.Inspect);
        var state = new PathComponentState(path, open.State.Kind == DataObjectKind.ReparsePoint || (open.State.Attributes & 0x400) != 0, open.State.OwnerSid, open.State.Dacl);
        if (StagingPathPolicy.CheckCreatedStaging(path, state, open.State.DaclProtected, open.State.FinalPath) is { } wrong)
        {
            throw new UnsafeInstallObjectException(wrong);
        }
    }

    /// <summary>Retries for up to 20 s: a file inside may still be held by a process that is exiting.</summary>
    public void MoveDirectory(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                AtomicMove.Rename(from, to);
                return;
            }
            catch (IOException) when (attempt < 20 && Directory.Exists(from) && !Directory.Exists(to))
            {
                Thread.Sleep(1000);
            }
        }
    }

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true); // links inside are removed, not followed

    public IReadOnlyList<string> ListDirectories(string root) => [.. Directory.EnumerateDirectories(root)];

    public string? ReadJournal(string path)
    {
        if (!File.Exists(path) && !IsDanglingLink(path))
        {
            return null;
        }
        CheckObject(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxJournalBytes)
        {
            return "";
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public void WriteJournal(string path, string contents)
    {
        CheckObject(path);
        var temp = path + ".tmp";
        CheckObject(temp);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(Encoding.UTF8.GetBytes(contents));
            stream.Flush(flushToDisk: true);
        }
        AtomicMove.Replace(temp, path);
    }

    public void DeleteJournal(string path)
    {
        CheckObject(path);
        CheckObject(path + ".tmp");
        File.Delete(path + ".tmp");
        File.Delete(path);
    }

    /// <summary>
    /// SYSTEM and Administrators full control, Users read and execute (what Program Files gives them), nothing else;
    /// protected (no inheritance from the install root), owner Administrators. Inherited by everything inside
    /// (SetNamedSecurityInfo propagates). The tray runs as the signed-in user and only reads and runs files there.
    /// </summary>
    public void ProtectInstalled(string path)
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
