using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Agent.Storage;
using Nexus.Core.Storage;

namespace Nexus.Agent.Account;

/// <summary>Encrypts the refresh token at rest. Production: DPAPI LocalMachine (the service runs as SYSTEM).</summary>
internal interface IRefreshProtector
{
    byte[] Protect(byte[] plain);
    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> for data it cannot open.</summary>
    byte[] Unprotect(byte[] sealedBytes);
}

/// <summary>CryptProtectData with CRYPTPROTECT_LOCAL_MACHINE | UI_FORBIDDEN and a fixed purpose string as entropy.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class DpapiLocalMachineProtector : IRefreshProtector
{
    private const int UiForbidden = 0x1;
    private const int LocalMachine = 0x4;
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("nexus-pc-refresh-v1");

    public byte[] Protect(byte[] plain) => Run(plain, protect: true);

    public byte[] Unprotect(byte[] sealedBytes) => Run(sealedBytes, protect: false);

    private static unsafe byte[] Run(byte[] input, bool protect)
    {
        fixed (byte* data = input)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (IntPtr)data };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (IntPtr)entropy };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden | LocalMachine, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok)
            {
                throw new System.Security.Cryptography.CryptographicException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                new Span<byte>((void*)outBlob.Data, outBlob.Size).Clear();
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}

/// <summary>Creation and access check of account.dat; production is <see cref="SystemOnlyFileAccess"/>, tests inject a fake.</summary>
internal interface IProtectedFileAccess
{
    /// <summary>FileMode.CreateNew, already with the SYSTEM-only access (Windows) or 0600 (elsewhere).</summary>
    FileStream CreateNew(string path);

    /// <summary>Owner and DACL of the open file; any error reading them is <see cref="ProtectedFileVerdict.Untrusted"/>.</summary>
    ProtectedFileVerdict Inspect(FileStream opened);
}

internal sealed class SystemOnlyFileAccess : IProtectedFileAccess
{
    public static readonly SystemOnlyFileAccess Instance = new();

    public FileStream CreateNew(string path) => ProtectedFile.CreateNew(path);

    public ProtectedFileVerdict Inspect(FileStream opened) => ProtectedFile.Classify(opened, acceptLegacy: true);
}

/// <summary>
/// account.dat in the service data folder: the refresh token sealed by <see cref="IRefreshProtector"/>, with its own
/// protected DACL for SYSTEM only (E4-6). The temp file is created already protected (CreateNew), written, flushed,
/// then renamed over account.dat in the same folder (the rename keeps the temp file's descriptor), so a crash leaves
/// either the old or the new token, never half of one. On load the owner and DACL are checked through the open handle
/// before a byte is read: SYSTEM-only is read; the old inherited SYSTEM+Administrators access (before E4-6) is read once
/// and rewritten SYSTEM-only; anything else is deleted unread and reported through <c>onUntrusted</c> ("not signed in").
/// A file larger than any real account.dat is treated the same way (deleted unread, reported). Access denied on open
/// or on the check means someone else changed the owner or DACL (the service is SYSTEM): not deleted, reported through
/// <c>onAccessDenied</c>; the data folder repair restores the access. Other I/O errors are temporary: null, nothing else.
/// The token itself is never logged.
/// </summary>
internal sealed class RefreshTokenStore(string dataDirectory, IRefreshProtector protector, IProtectedFileAccess? access = null,
    Action? onUntrusted = null, Action? onAccessDenied = null)
{
    public const string FileName = DataDirectoryPolicy.AccountFileName;
    /// <summary>A real account.dat is well under 1 KB; anything above this is a replacement or damage.</summary>
    internal const int MaxBytes = 8 * 1024;
    private readonly IProtectedFileAccess _access = access ?? SystemOnlyFileAccess.Instance;

    public string FilePath { get; } = Path.Combine(dataDirectory, FileName);

    private string TempPath => Path.Combine(Path.GetDirectoryName(FilePath)!, DataDirectoryPolicy.AccountTempFileName);

    /// <summary>The stored token, or null (none, unreadable, untrusted or sealed by another machine: "not signed in").</summary>
    public string? Load()
    {
        DeleteQuietly(TempPath); // left from a crash between create and rename
        byte[] sealedBytes;
        ProtectedFileVerdict verdict;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes)
            {
                verdict = ProtectedFileVerdict.Untrusted; // whatever its access: not read, deleted, reported
                sealedBytes = [];
            }
            else
            {
                verdict = _access.Inspect(stream);
                if (verdict == ProtectedFileVerdict.Untrusted)
                {
                    sealedBytes = [];
                }
                else
                {
                    sealedBytes = new byte[stream.Length];
                    stream.ReadExactly(sealedBytes);
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // SYSTEM is refused: the owner or DACL was changed by someone else. Kept (it cannot be deleted without
            // access anyway, and trying must not hide the problem); reported without path or content.
            onAccessDenied?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return null; // in use or a disk error: the next load tries again
        }

        if (verdict == ProtectedFileVerdict.Untrusted)
        {
            // Someone else wrote or re-permissioned it: not read, deleted; the caller audits without path or content.
            DeleteQuietly(FilePath);
            onUntrusted?.Invoke();
            return null;
        }

        var token = Unseal(sealedBytes);
        if (token is not null && verdict == ProtectedFileVerdict.Legacy)
        {
            try
            {
                Save(token); // migrate the old inherited access to SYSTEM-only; the sign-in stays
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // the next load tries again; the token read is still valid
            }
        }
        return token;
    }

    private string? Unseal(byte[] sealedBytes)
    {
        try
        {
            var plain = protector.Unprotect(sealedBytes);
            try
            {
                return JsonNode.Parse(plain) is JsonObject { } o && o["v"]?.GetValue<int>() == 1
                    && o["refreshToken"]?.GetValue<string>() is { Length: > 0 and <= 256 } token
                    ? token
                    : null;
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException
            or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // never stops the service; Save deletes it again before CreateNew
        }
    }

    public void Save(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["v"] = 1, ["refreshToken"] = refreshToken });
        byte[] sealedBytes;
        try
        {
            sealedBytes = protector.Protect(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
        var temp = TempPath;
        File.Delete(temp);
        using (var stream = _access.CreateNew(temp))
        {
            stream.Write(sealedBytes);
            stream.Flush(flushToDisk: true);
        }
        // Same folder: MoveFileEx(REPLACE_EXISTING) is atomic and the new file keeps the temp file's own descriptor.
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        File.Delete(TempPath);
        File.Delete(FilePath);
    }
}
