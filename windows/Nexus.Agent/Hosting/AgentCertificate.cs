using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nexus.Agent.Storage;

namespace Nexus.Agent.Hosting;

/// <summary>
/// The agent's self-signed TLS certificate (ECDSA P-256). Clients pin its SHA-256 fingerprint at
/// pairing, so no certificate authority is involved (ARCHITECTURE.md D4).
/// </summary>
internal static class AgentCertificate
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(5 * 365);

    public static X509Certificate2 Create(Guid pcId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=Nexus PC Agent {pcId:D}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now + Validity);
    }

    /// <summary>Loads the persisted certificate or creates one, updating the config with its thumbprint.</summary>
    public static X509Certificate2 LoadOrCreate(string dataDirectory, ref AgentConfig config)
    {
        if (OperatingSystem.IsWindows())
        {
            return LoadOrCreateInMachineStore(dataDirectory, ref config);
        }
        return LoadOrCreateFile(dataDirectory, ref config);
    }

    // Windows: the private key lives in the machine key store (CNG), non-exportable, never in a file.
    [SupportedOSPlatform("windows")]
    private static X509Certificate2 LoadOrCreateInMachineStore(string dataDirectory, ref AgentConfig config)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        if (config.CertificateThumbprint is { } thumbprint)
        {
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count == 1 && found[0].HasPrivateKey)
            {
                return found[0];
            }
        }

        using var ephemeral = Create(config.PcId);
        // SChannel cannot use ephemeral keys: round-trip through PKCS#12 to persist the key, non-exportable.
        var pfx = ephemeral.Export(X509ContentType.Pkcs12);
        try
        {
            var persisted = X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
            store.Add(persisted);
            config = config with { CertificateThumbprint = persisted.Thumbprint };
            config.Save(dataDirectory);
            return persisted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    // Development on non-Windows systems: PKCS#12 file in the user-only data directory.
    [UnsupportedOSPlatform("windows")]
    private static X509Certificate2 LoadOrCreateFile(string dataDirectory, ref AgentConfig config)
    {
        var path = Path.Combine(dataDirectory, "agent-dev.pfx");
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);
        }
        using var created = Create(config.PcId);
        var pfx = created.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(path, pfx);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        config = config with { CertificateThumbprint = created.Thumbprint };
        config.Save(dataDirectory);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }
}
