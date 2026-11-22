using System.IO.Compression;
using System.Runtime.Versioning;
using Nexus.Core.Storage;
using Nexus.Windows;

namespace Nexus.Setup;

/// <summary>The install path is not safe to unpack into (T5 section 1); nothing was changed.</summary>
internal sealed class StagingRefusedException(string message) : Exception(message);

/// <summary>The payload does not match its manifest (T5 section 2); the installed version was not touched.</summary>
internal sealed class PayloadRejectedException(string message) : Exception(message);

/// <summary>
/// T5 section 1: %ProgramFiles%\NexusPC\.staging-&lt;GUID&gt;, created with a protected DACL (SYSTEM, Administrators) and owner
/// Administrators, after every component of the path was opened by handle (FILE_FLAG_OPEN_REPARSE_POINT) and accepted by
/// <see cref="StagingPathPolicy"/>. The whole path is checked again after creation, and the final path must be the expected one.
/// Leftover .staging-* folders of an interrupted run are never reused (recovery: part B of T5).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StagingDirectory : IDisposable
{
    public const string Prefix = ".staging-";

    private readonly TextWriter _log;

    private StagingDirectory(string path, TextWriter log)
    {
        Path = path;
        _log = log;
    }

    public string Path { get; }

    /// <param name="installRoot">%ProgramFiles%\NexusPC, built from Environment.GetFolderPath (never from environment variables).</param>
    public static StagingDirectory Create(string installRoot, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(log);
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(installRoot));
        var staging = root + "\\" + Prefix + Guid.NewGuid().ToString("N");
        var components = StagingPathPolicy.Components(staging);
        if (components.Count < 3)
        {
            throw new StagingRefusedException("unsupported install path " + staging);
        }
        var fs = new DataDirectoryFileSystem();
        var ownFrom = components.Count - 2; // NexusPC and staging: our own folders, strict write rules

        // Everything above NexusPC first; then NexusPC (created if missing, inheriting Program Files' DACL).
        CheckPath(fs, components.Take(components.Count - 2).ToList(), ownFrom);
        if (!Directory.Exists(root))
        {
            try
            {
                fs.CreateDirectory(root);
            }
            catch (DataAccessException) when (Directory.Exists(root))
            {
                // created at the same moment by someone allowed to write Program Files; it is checked below like any other
            }
        }
        CheckPath(fs, components.Take(components.Count - 1).ToList(), ownFrom);

        foreach (var leftover in Leftovers(root))
        {
            log.WriteLine("Leftover from an interrupted run (kept, not reused): " + leftover);
        }

        var error = DataDirectoryFileSystem.TryCreateProtectedDirectory(staging, withOwner: true);
        if (error != 0)
        {
            throw new StagingRefusedException($"cannot create {staging} (error {error})");
        }
        var created = new StagingDirectory(staging, log);
        try
        {
            CheckPath(fs, components, ownFrom);
            using var open = fs.Open(staging, DataAccess.Inspect);
            if (StagingPathPolicy.CheckCreatedStaging(staging, ToComponent(open.State), open.State.DaclProtected, open.State.FinalPath) is { } wrong)
            {
                throw new StagingRefusedException(wrong);
            }
            log.WriteLine("Staging: " + staging + " (path checked, owner Administrators, protected DACL)");
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<string> Leftovers(string installRoot) =>
        Directory.Exists(installRoot) ? [.. Directory.EnumerateDirectories(installRoot, Prefix + "*")] : [];

    /// <summary>Opens every component by handle without following links and applies the rules; throws on the first refusal.</summary>
    public static void CheckPath(DataDirectoryFileSystem fs, IReadOnlyList<string> components, int strictFromLevel = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(components);
        var states = new List<PathComponentState>();
        for (var i = 0; i < components.Count; i++)
        {
            try
            {
                using var open = fs.Open(components[i], DataAccess.Inspect);
                states.Add(ToComponent(open.State) with { IsVolumeRoot = i == 0 });
                if (open.State.Kind is not (DataObjectKind.Directory or DataObjectKind.ReparsePoint))
                {
                    throw new StagingRefusedException("not a folder: " + components[i]);
                }
            }
            catch (DataAccessException e)
            {
                throw new StagingRefusedException(e.Message);
            }
        }
        if (StagingPathPolicy.Check(states, strictFromLevel) is { } refusal)
        {
            throw new StagingRefusedException(refusal);
        }
    }

    private static PathComponentState ToComponent(DataObjectState state) =>
        new(state.FullPath, state.Kind == DataObjectKind.ReparsePoint || (state.Attributes & 0x400) != 0, state.OwnerSid,
            state.DaclPresent ? state.Dacl : [new DataAce("(NULL DACL)", true, uint.MaxValue, 0)]);

    /// <summary>
    /// Unpacks every manifest file into staging. Each file is new (CreateNew, no sharing while written), flushed, and its
    /// SHA-256 is computed from what was written, read back through the same handle. Paths stay inside staging.
    /// Returns, for <paramref name="holdEntry"/>, a read handle opened right after writing with FileShare.Read: from then on
    /// nobody can write, delete or rename that file until the handle is closed.
    /// </summary>
    public FileStream? Extract(ZipArchive zip, IReadOnlyList<ManifestEntry> manifest, string? holdEntry)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(manifest);
        FileStream? hold = null;
        try
        {
            foreach (var entry in manifest)
            {
                var zipEntry = PayloadManifest.FindEntry(zip, entry.Path) ?? throw new PayloadRejectedException("missing: " + entry.Path);
                var target = LocalPath(entry.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                using (var output = new FileStream(target, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None }))
                {
                    using (var input = zipEntry.Open())
                    {
                        CopyAtMost(input, output, entry.Size + 1);
                    }
                    output.Flush(flushToDisk: true);
                    if (PayloadManifest.CheckWritten(entry, output) is { } bad)
                    {
                        throw new PayloadRejectedException(bad);
                    }
                }
                if (entry.Path == holdEntry)
                {
                    hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (PayloadManifest.CheckWritten(entry, hold) is { } changed)
                    {
                        throw new PayloadRejectedException(changed);
                    }
                }
            }
            _log.WriteLine($"Unpacked into {Path}: {manifest.Count} file(s), SHA-256 of each written file matches manifest.json");
            return hold;
        }
        catch
        {
            hold?.Dispose();
            throw;
        }
    }

    public string LocalPath(string manifestPath)
    {
        var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, manifestPath.Replace('/', '\\')));
        if (!PayloadManifest.IsSafeRelativePath(manifestPath) || !target.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new PayloadRejectedException("unsafe path: " + manifestPath);
        }
        return target;
    }

    private static void CopyAtMost(Stream input, Stream output, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while (total < limit && (read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - total))) > 0)
        {
            output.Write(buffer, 0, read);
            total += read;
        }
    }

    /// <summary>Removes only this run's staging folder; a failure is logged and left for the next run.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.WriteLine($"Staging not removed ({e.Message}); the next run removes it: {Path}");
        }
    }
}
