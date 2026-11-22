using System.Net;
using System.Security.Cryptography;

namespace Nexus.Core.Security;

/// <summary>Mandatory integrity level of a process token (Low, Medium, High, System…).</summary>
public enum ProcessIntegrity
{
    Unknown,
    Untrusted,
    Low,
    Medium,
    High,
    System,
}

/// <summary>
/// A process opened by handle. While the handle is held, Windows does not reuse its PID, so the start time read
/// through it identifies this exact process.
/// </summary>
public interface IPeerProcess : IDisposable
{
    int ProcessId { get; }

    DateTime StartTimeUtc { get; }

    string? ImagePath { get; }

    ProcessIntegrity Integrity { get; }

    /// <summary>Completes when the process exits (watched through the held handle).</summary>
    Task Exited { get; }
}

/// <summary>Which process holds the server end of one TCP connection (T2).</summary>
public interface IConnectionOwnerSource
{
    /// <summary>
    /// The owning PID of the counterpart row of our connection: local end = the server address we connected to,
    /// remote end = our own local address and port. Null when no such row exists.
    /// </summary>
    int? ServerProcessId(IPEndPoint clientLocal, IPEndPoint clientRemote);

    /// <summary>Opens the process (query + wait rights only); null if it cannot be opened.</summary>
    IPeerProcess? Open(int processId);
}

/// <summary>The OpenRGB server the agent started itself (case A): PID and start time read through our handle.</summary>
public sealed record ChildIdentity(int ProcessId, DateTime StartTimeUtc);

public enum PeerKind
{
    /// <summary>Case A: the agent's own child process.</summary>
    Bundled,

    /// <summary>Case B: the OpenRGB window an administrator opened from the tray (elevated, bundled path, pinned hash).</summary>
    SetupWindow,

    /// <summary>Anything else: the agent sends nothing to it.</summary>
    Conflict,
}

public sealed record PeerVerdict(PeerKind Kind, string Reason);

/// <summary>
/// Decides whether the process that accepted our connection is "our" OpenRGB (T2, section 1). A file hash on disk
/// is never taken as proof of the running image on its own: case A relies on the agent having checked, locked and
/// started the file itself; case B also needs the elevated integrity level and the bundled path.
/// </summary>
public sealed class OpenRgbIdentityPolicy(string bundledPath, string pinnedSha256, Func<string, string?> fileSha256)
{
    public string BundledPath => bundledPath;

    public string PinnedSha256 => pinnedSha256;

    public PeerVerdict Check(int? ownerPid, IPeerProcess? process, ChildIdentity? child)
    {
        if (ownerPid is null)
        {
            return Conflict("no TCP table row for this connection");
        }
        if (process is null || process.ProcessId != ownerPid)
        {
            return Conflict("the process that accepted the connection cannot be opened");
        }
        if (child is not null && child.ProcessId == process.ProcessId)
        {
            return child.StartTimeUtc == process.StartTimeUtc
                ? new PeerVerdict(PeerKind.Bundled, "bundled server")
                : Conflict("PID of the bundled server reused by another process");
        }
        if (process.ImagePath is not { } image || !SamePath(image, bundledPath))
        {
            return Conflict("not the OpenRGB installed with Nexus PC");
        }
        if (process.Integrity is not (ProcessIntegrity.High or ProcessIntegrity.System))
        {
            return Conflict("OpenRGB window not running as administrator");
        }
        if (!HashMatches(fileSha256(bundledPath)))
        {
            return Conflict("OpenRGB.exe does not match the pinned SHA-256");
        }
        return new PeerVerdict(PeerKind.SetupWindow, "OpenRGB window opened by an administrator");
    }

    public bool HashMatches(string? sha256) => sha256 is not null && string.Equals(sha256, pinnedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower-case hex SHA-256 of the stream from its current position.</summary>
    public static string Sha256(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    /// <summary>SHA-256 of a file opened for reading (others may read, not write); null if it cannot be read.</summary>
    public static string? Sha256OfFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Sha256(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Normalized, case-insensitive path comparison (Windows paths).</summary>
    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path;

    private static PeerVerdict Conflict(string reason) => new(PeerKind.Conflict, reason);
}
