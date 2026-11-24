using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

/// <summary>
/// account.dat in the service data folder (inherits the folder's SYSTEM/Administrators-only access): the refresh
/// token sealed by <see cref="IRefreshProtector"/>. Replaced atomically (temp file, then rename in the same folder),
/// so a crash leaves either the old or the new token, never half of one. The token itself is never logged.
/// </summary>
internal sealed class RefreshTokenStore(string dataDirectory, IRefreshProtector protector)
{
    public const string FileName = "account.dat";
    private const int MaxBytes = 8 * 1024;

    public string FilePath { get; } = Path.Combine(dataDirectory, FileName);

    /// <summary>The stored token, or null (none, unreadable or sealed by another machine: treated as "not signed in").</summary>
    public string? Load()
    {
        File.Delete(FilePath + ".tmp");
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }
            var plain = protector.Unprotect(File.ReadAllBytes(FilePath));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
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
        var temp = FilePath + ".tmp";
        File.Delete(temp);
        using (var stream = OperatingSystem.IsWindows()
            ? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            : new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
        {
            stream.Write(sealedBytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        File.Delete(FilePath + ".tmp");
        File.Delete(FilePath);
    }
}
