#if NEXUS_DEV_FILE_KEY
using System.Security.Cryptography;

namespace Nexus.Agent.Security;

/// <summary>
/// Development and CI only, never Windows (owner decision for E4-3a): a permanent P-256 PC key in a separate file,
/// so the agent can run on macOS for the iOS live tests. It exists only when the variable
/// <see cref="EnvironmentVariable"/> names the file explicitly; nothing falls back to it. The code is not compiled
/// into Windows builds (Nexus.Agent.csproj: NEXUS_DEV_FILE_KEY only without a win-* RuntimeIdentifier) and refuses
/// to run on Windows anyway. The file is created 0600 in a 0700 folder, never inside the data folder, and its
/// path is never logged.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class DevFileDeviceKey : IDeviceKey
{
    public const string EnvironmentVariable = "NEXUS_DEV_FILE_KEY";
    public const string ProviderName = "development file key";

    private const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryMode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly ECDsa _ecdsa;

    private DevFileDeviceKey(ECDsa ecdsa, bool created)
    {
        _ecdsa = ecdsa;
        Created = created;
        Spki = ecdsa.ExportSubjectPublicKeyInfo();
        if (!Nexus.Core.Security.PcIdDerivation.IsP256Spki(Spki))
        {
            ecdsa.Dispose();
            throw new CryptographicException("The development key is not a P-256 key.");
        }
    }

    public byte[] Spki { get; }
    public string Provider => ProviderName;
    public bool Created { get; }

    /// <summary>The absolute key path from the flag; null when the flag is not set. Refused on Windows.</summary>
    public static string? PathFromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        RefuseWindows();
        if (environment(EnvironmentVariable) is not { Length: > 0 } path)
        {
            return null;
        }
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(EnvironmentVariable + " must be an absolute path.");
        }
        return path;
    }

    /// <summary>Opens the key file or creates it (CreateNew, 0600); a file readable by others is refused.</summary>
    /// <param name="dataDirectory">The agent's data folder: the key must not be inside it.</param>
    public static DevFileDeviceKey OpenOrCreate(string path, string? dataDirectory = null)
    {
        RefuseWindows();
        var full = Path.GetFullPath(path);
        if (dataDirectory is not null)
        {
            var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(data, StringComparison.Ordinal))
            {
                throw new ArgumentException("The development key must not be inside the data folder.");
            }
        }
        var folder = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder, DirectoryMode0700);
        }
        var ecdsa = ECDsa.Create();
        try
        {
            if (TryRead(full) is { } existing)
            {
                ecdsa.ImportPkcs8PrivateKey(existing, out _);
                return new DevFileDeviceKey(ecdsa, created: false);
            }
            ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
            var bytes = ecdsa.ExportPkcs8PrivateKey();
            try
            {
                using var stream = new FileStream(full, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = FileMode0600,
                });
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(full))
            {
                // Another process created it first: use that one, never overwrite.
                ecdsa.ImportPkcs8PrivateKey(TryRead(full) ?? throw new CryptographicException("The development key file is empty."), out _);
                return new DevFileDeviceKey(ecdsa, created: false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            return new DevFileDeviceKey(ecdsa, created: true);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    private static byte[]? TryRead(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }
        if (info.LinkTarget is not null)
        {
            throw new UnauthorizedAccessException("The development key file must not be a link.");
        }
        if ((info.UnixFileMode & ~FileMode0600) != 0)
        {
            throw new UnauthorizedAccessException("The development key file must be readable by its owner only (0600).");
        }
        return File.ReadAllBytes(path);
    }

    private static void RefuseWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The development file key is not available on Windows: the PC key is the CNG key only.");
        }
    }

    public byte[] SignP1363(ReadOnlySpan<byte> data) =>
        _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _ecdsa.Dispose();
}
#endif
