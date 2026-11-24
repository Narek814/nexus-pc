using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Nexus.Agent.Security;

/// <summary>The PC's permanent key: only the public SPKI and P1363 signatures leave it.</summary>
internal interface IDeviceKey : IDisposable
{
    /// <summary>SubjectPublicKeyInfo DER (P-256, uncompressed point).</summary>
    byte[] Spki { get; }
    /// <summary>Key storage provider holding the key (for the log).</summary>
    string Provider { get; }
    /// <summary>True when the key did not exist and was created by this open.</summary>
    bool Created { get; }
    /// <summary>ECDSA P-256 / SHA-256, IEEE P1363 (r || s, 64 bytes).</summary>
    byte[] SignP1363(ReadOnlySpan<byte> data);
}

/// <summary>Machine CNG key "NexusPC-Device-v1": TPM when available, otherwise the software KSP. Never exported.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DeviceKeyStore : IDeviceKey
{
    public const string KeyName = "NexusPC-Device-v1";

    private readonly CngKey _key;
    private readonly ECDsaCng _ecdsa;

    private DeviceKeyStore(CngKey key, string provider, bool created)
    {
        _key = key;
        _ecdsa = new ECDsaCng(key);
        Provider = provider;
        Created = created;
        Spki = _ecdsa.ExportSubjectPublicKeyInfo();
        if (!Nexus.Core.Security.PcIdDerivation.IsP256Spki(Spki))
        {
            Dispose();
            throw new CryptographicException($"{KeyName} is not a P-256 key.");
        }
    }

    public byte[] Spki { get; }
    public string Provider { get; }
    public bool Created { get; }

    /// <summary>Opens the existing key (any provider) or creates it: Platform Crypto Provider first, then Software KSP.</summary>
    public static DeviceKeyStore OpenOrCreate(string keyName = KeyName) => OpenOrCreate(keyName, machineKey: true);

    /// <summary>
    /// <paramref name="machineKey"/> false is for tests only (no administrator rights needed): a key of the current
    /// user in the Software KSP, same algorithm and policy. Production always uses the machine key.
    /// </summary>
    internal static DeviceKeyStore OpenOrCreate(string keyName, bool machineKey)
    {
        CngProvider[] providers = machineKey
            ? [CngProvider.MicrosoftPlatformCryptoProvider, CngProvider.MicrosoftSoftwareKeyStorageProvider]
            : [CngProvider.MicrosoftSoftwareKeyStorageProvider];
        foreach (var provider in providers)
        {
            if (TryOpen(keyName, provider, machineKey) is { } existing)
            {
                return new DeviceKeyStore(existing, provider.Provider, created: false);
            }
        }
        foreach (var provider in providers)
        {
            try
            {
                var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
                {
                    Provider = provider,
                    KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing,
                });
                return new DeviceKeyStore(key, provider.Provider, created: true);
            }
            catch (CryptographicException) when (provider == CngProvider.MicrosoftPlatformCryptoProvider)
            {
                // No usable TPM: fall back to the software provider (owner decision 2).
            }
        }
        throw new CryptographicException($"Cannot create {keyName}.");
    }

    /// <summary>Removes the key (tests only).</summary>
    internal static void Delete(string keyName, bool machineKey = true)
    {
        foreach (var provider in new[] { CngProvider.MicrosoftPlatformCryptoProvider, CngProvider.MicrosoftSoftwareKeyStorageProvider })
        {
            TryOpen(keyName, provider, machineKey)?.Delete();
        }
    }

    private static CngKey? TryOpen(string keyName, CngProvider provider, bool machineKey)
    {
        var scope = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        try
        {
            return CngKey.Exists(keyName, provider, scope)
                ? CngKey.Open(keyName, provider, scope)
                : null;
        }
        catch (CryptographicException)
        {
            return null; // provider unavailable (no TPM)
        }
    }

    public byte[] SignP1363(ReadOnlySpan<byte> data) =>
        _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>For the non-exportability test only: always throws.</summary>
    internal byte[] TryExportPrivateKey() => _key.Export(CngKeyBlobFormat.EccPrivateBlob);

    public void Dispose()
    {
        _ecdsa.Dispose();
        _key.Dispose();
    }
}
