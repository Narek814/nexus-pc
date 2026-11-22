using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Windows;

/// <summary>Result of one WinVerifyTrust call: its HRESULT and, when it succeeded, the verified signer's leaf certificate.</summary>
/// <param name="Code">0 = signature valid and the chain ends at a trusted root; otherwise the WinVerifyTrust error.</param>
/// <param name="LeafSha256">SHA-256 of the whole DER of the signer's leaf certificate (lowercase hex); null unless Code is 0.</param>
public sealed record AuthenticodeResult(int Code, string? LeafSha256, string? LeafSubject);

/// <summary>
/// Authenticode through WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2) on an already open file handle (T5 section 4):
/// the file that is checked is exactly the one behind the handle the caller keeps open, not whatever a path points to later.
/// The leaf certificate is the one WinVerifyTrust itself verified (provider data of signer 0, chain element 0).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Authenticode
{
    public const int TrustENoSignature = unchecked((int)0x800B0100);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2;
    private const uint RevokeNone = 0, RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1, StateActionClose = 2;
    private const uint RevocationCheckChainExcludeRoot = 0x80;
    private const uint DisableMd2Md4 = 0x2000;
    private static readonly IntPtr NoWindow = new(-1);

    /// <summary>
    /// One WinVerifyTrust call on <paramref name="file"/> (<paramref name="path"/> only tells WinVerifyTrust the file type).
    /// <paramref name="checkRevocation"/>: whole chain except the root. Without it, revocation is not checked at all.
    /// </summary>
    public static AuthenticodeResult Verify(SafeFileHandle file, string path, bool checkRevocation)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(path);
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfoPtr = IntPtr.Zero;
        var added = false;
        try
        {
            file.DangerousAddRef(ref added);
            var fileInfo = new WintrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                FilePath = pathPtr,
                File = file.DangerousGetHandle(),
                KnownSubject = IntPtr.Zero,
            };
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WintrustData
            {
                Size = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = UiNone,
                RevocationChecks = checkRevocation ? RevokeWholeChain : RevokeNone,
                UnionChoice = ChoiceFile,
                Union = fileInfoPtr,
                StateAction = StateActionVerify,
                ProvFlags = DisableMd2Md4 | (checkRevocation ? RevocationCheckChainExcludeRoot : 0),
            };
            var action = GenericVerifyV2;
            var code = WinVerifyTrust(NoWindow, ref action, ref data);
            try
            {
                if (code != 0)
                {
                    return new AuthenticodeResult(code, null, null);
                }
                using var leaf = SignerLeaf(data.StateData);
                return leaf is null
                    ? new AuthenticodeResult(TrustENoSignature, null, null)
                    : new AuthenticodeResult(0, Convert.ToHexStringLower(SHA256.HashData(leaf.RawData)), leaf.Subject);
            }
            finally
            {
                data.StateAction = StateActionClose;
                _ = WinVerifyTrust(NoWindow, ref action, ref data);
            }
        }
        finally
        {
            if (added)
            {
                file.DangerousRelease();
            }
            if (fileInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPtr);
            }
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static X509Certificate2? SignerLeaf(IntPtr stateData)
    {
        var provider = WTHelperProvDataFromStateData(stateData);
        if (provider == IntPtr.Zero)
        {
            return null;
        }
        var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if (signer == IntPtr.Zero)
        {
            return null;
        }
        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero)
        {
            return null;
        }
        // CRYPT_PROVIDER_CERT: DWORD cbStruct, then PCCERT_CONTEXT pCert (pointer-aligned).
        var context = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        return context == IntPtr.Zero ? null : new X509Certificate2(context); // duplicates the context
    }

    /// <summary>Full image path of a process, read through its handle (no PID lookup).</summary>
    public static string? ImagePath(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var buffer = new char[32768];
        var length = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr Union;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex, int counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] buffer, ref uint length);
}
