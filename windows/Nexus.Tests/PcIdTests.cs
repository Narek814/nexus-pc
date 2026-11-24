using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Agent.Security;
using Nexus.Agent.Storage;
using Nexus.Core.Security;

namespace Nexus.Tests;

/// <summary>
/// Test stand-in for the machine CNG key: a P-256 key in its own folder next to the data directory (never inside
/// it, so data-directory checks see nothing new), one per data directory, so pcId stays stable across agent
/// restarts in the same test. Parallel tests never share a file; <see cref="Delete"/> (called by
/// AgentTestHost.DeleteDirectory) removes it, and whatever a test left behind is removed when the test run exits.
/// </summary>
internal sealed class FileDeviceKey : IDeviceKey
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Folders = new(StringComparer.Ordinal);

    static FileDeviceKey() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        foreach (var folder in Folders.Keys)
        {
            DeleteFolder(folder);
        }
    };

    private readonly ECDsa _ecdsa;

    private FileDeviceKey(ECDsa ecdsa, bool created)
    {
        _ecdsa = ecdsa;
        Created = created;
        Spki = ecdsa.ExportSubjectPublicKeyInfo();
    }

    public byte[] Spki { get; }
    public string Provider => "test";
    public bool Created { get; }

    /// <summary>"&lt;data directory&gt;-key": a sibling folder, unique per data directory.</summary>
    public static string KeyFolder(string dataDirectory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + "-key";

    public static string KeyPath(string dataDirectory) => Path.Combine(KeyFolder(dataDirectory), "device.key");

    public static FileDeviceKey OpenOrCreate(string dataDirectory)
    {
        var path = KeyPath(dataDirectory);
        Folders.TryAdd(KeyFolder(dataDirectory), 0);
        var ecdsa = ECDsa.Create();
        if (File.Exists(path))
        {
            ecdsa.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
            return new FileDeviceKey(ecdsa, created: false);
        }
        ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write(ecdsa.ExportPkcs8PrivateKey());
        }
        catch (IOException) when (File.Exists(path))
        {
            // Created by someone else in the meantime: never overwrite, use that one.
            ecdsa.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
            return new FileDeviceKey(ecdsa, created: false);
        }
        return new FileDeviceKey(ecdsa, created: true);
    }

    /// <summary>Removes the key of this data directory (its folder).</summary>
    public static void Delete(string dataDirectory)
    {
        var folder = KeyFolder(dataDirectory);
        DeleteFolder(folder);
        Folders.TryRemove(folder, out _);
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort at exit
        }
    }

    public byte[] SignP1363(ReadOnlySpan<byte> data) =>
        _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _ecdsa.Dispose();
}

/// <summary>E4-3: pcId derived from the PC key (shared/fixtures/pc-id/derive.v1.json) and migration of old installs.</summary>
public sealed class PcIdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-pcid-" + Guid.NewGuid().ToString("N"));

    // Created like the agent's own data folder (protected DACL on Windows), not with an inherited %TEMP% ACL:
    // the agent tests below start the agent on it, and its start-up check must pass unchanged (CI on aef3cec).
    public PcIdTests() => AgentTestHost.CreateDataDirectory(_dir);

    public void Dispose() => AgentTestHost.DeleteDirectory(_dir);

    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shared", "pc-id", "derive.v1.json")));

    private static byte[] B64u(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

    [Fact]
    public void FromSpki_MatchesEveryValidVector()
    {
        using var doc = Fixture();
        var vectors = doc.RootElement.GetProperty("valid").EnumerateArray().ToList();
        Assert.True(vectors.Count >= 5);
        foreach (var v in vectors)
        {
            var id = PcIdDerivation.FromSpki(B64u(v.GetProperty("spkiB64u").GetString()!));
            Assert.Equal(v.GetProperty("pcId").GetString(), id.ToString("D"));
            Assert.True(PcIdDerivation.IsDerived(id));
        }
    }

    [Fact]
    public void FromSpki_RejectsEveryInvalidVector()
    {
        using var doc = Fixture();
        var vectors = doc.RootElement.GetProperty("invalid").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            var spki = B64u(v.GetProperty("spkiB64u").GetString()!);
            Assert.False(PcIdDerivation.IsP256Spki(spki), v.GetProperty("name").GetString());
            Assert.Throws<ArgumentException>(() => PcIdDerivation.FromSpki(spki));
        }
    }

    [Fact]
    public void FromSpki_MatchesFreshDotNetKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        Assert.Equal(PcIdDerivation.FromSpki(spki), PcIdDerivation.FromSpki(spki));
        Assert.False(PcIdDerivation.IsDerived(Guid.NewGuid()));
    }

    [Fact]
    public void Config_NewInstall_UsesDerivedIdAndIsStable()
    {
        var derived = PcIdDerivation.FromSpki(FileDeviceKey.OpenOrCreate(_dir).Spki);
        var first = AgentConfig.LoadOrCreate(_dir, derived, out var change1);
        Assert.Equal(AgentConfig.PcIdChange.Created, change1);
        Assert.Equal(derived, first.PcId);
        Assert.Null(first.LegacyPcId);

        var again = PcIdDerivation.FromSpki(FileDeviceKey.OpenOrCreate(_dir).Spki);
        var second = AgentConfig.LoadOrCreate(_dir, again, out var change2);
        Assert.Equal(AgentConfig.PcIdChange.None, change2);
        Assert.Equal(derived, second.PcId);
    }

    [Fact]
    public void Config_OldInstall_MovesRandomIdToLegacyOnce()
    {
        var old = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{"pcId":"{{old}}","port":47801,"certificateThumbprint":"AB"}""");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var derived = PcIdDerivation.FromSpki(ecdsa.ExportSubjectPublicKeyInfo());

        var migrated = AgentConfig.LoadOrCreate(_dir, derived, out var change);
        Assert.Equal(AgentConfig.PcIdChange.MigratedFromLegacy, change);
        Assert.Equal(derived, migrated.PcId);
        Assert.Equal(old, migrated.LegacyPcId);
        Assert.Equal(47801, migrated.Port);
        Assert.Equal("AB", migrated.CertificateThumbprint);

        var reloaded = AgentConfig.LoadOrCreate(_dir, derived, out var change2);
        Assert.Equal(AgentConfig.PcIdChange.None, change2);
        Assert.Equal(old, reloaded.LegacyPcId);
        Assert.Contains("legacyPcId", File.ReadAllText(Path.Combine(_dir, "config.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Config_KeyLost_GetsNewIdAndKeepsLegacy()
    {
        var old = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{"pcId":"{{old}}"}""");
        using var k1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var k2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        AgentConfig.LoadOrCreate(_dir, PcIdDerivation.FromSpki(k1.ExportSubjectPublicKeyInfo()), out _);

        var id2 = PcIdDerivation.FromSpki(k2.ExportSubjectPublicKeyInfo());
        var replaced = AgentConfig.LoadOrCreate(_dir, id2, out var change);
        Assert.Equal(AgentConfig.PcIdChange.KeyReplaced, change);
        Assert.Equal(id2, replaced.PcId);
        Assert.Equal(old, replaced.LegacyPcId); // the pre-E4 id is not overwritten by a derived one
    }

    [Fact]
    public async Task Agent_OldInstall_MigratesLogsOnceAndKeepsPairedDevices()
    {
        var old = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{"pcId":"{{old}}"}""");
        Guid first;
        await using (var host = await AgentTestHost.StartAsync(_dir))
        {
            first = host.PcId;
            Assert.NotEqual(old, first);
            Assert.True(PcIdDerivation.IsDerived(first));
            Assert.Equal(PcIdDerivation.FromSpki(host.Services.Identity.DeviceKey!.Spki), first);
        }
        await using (var host = await AgentTestHost.StartAsync(_dir))
        {
            Assert.Equal(first, host.PcId);
        }
        var audit = File.ReadAllText(Path.Combine(_dir, "audit.log"));
        Assert.Single(audit.Split('\n'), l => l.Contains("\"pcId.migrated\"", StringComparison.Ordinal));
        Assert.Contains(old.ToString("D"), audit, StringComparison.Ordinal);
        Assert.DoesNotContain("pcId.keyReplaced", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_KeyLost_WarnsInAudit()
    {
        await using (await AgentTestHost.StartAsync(_dir)) { }
        File.Delete(FileDeviceKey.KeyPath(_dir));
        await using (await AgentTestHost.StartAsync(_dir)) { }
        Assert.Contains("\"pcId.keyReplaced\"", File.ReadAllText(Path.Combine(_dir, "audit.log")), StringComparison.Ordinal);
    }
}

/// <summary>
/// E4-3 on Windows only (CI windows-latest): the CNG key. The machine key needs administrator rights; without
/// them that test says so in its output (xunit 2.9 has no dynamic skip). The user-key variant (Software KSP,
/// tests only) checks P-256, P1363 signatures, non-exportability and stability without rights.
/// </summary>
public sealed class DeviceKeyStoreWindowsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void MachineKey_IsP256_Stable_SignsP1363_AndIsNotExportable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Windows-only: CNG
        }
        if (!Elevated())
        {
            output.WriteLine("Not elevated: the machine key test needs administrator rights and did not run (UserKey_* covers the key without rights).");
            return;
        }
        Run(machineKey: true);
    }

    [Fact]
    public void UserKey_IsP256_Stable_SignsP1363_AndIsNotExportable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Windows-only: CNG
        }
        Run(machineKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static bool Elevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private static void Run(bool machineKey)
    {
        var name = "NexusPC-Device-test-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] spki;
            using (var key = DeviceKeyStore.OpenOrCreate(name, machineKey))
            {
                Assert.True(key.Created);
                Assert.Contains(key.Provider, machineKey
                    ? new[] { "Microsoft Platform Crypto Provider", "Microsoft Software Key Storage Provider" }
                    : new[] { "Microsoft Software Key Storage Provider" });
                spki = key.Spki;
                Assert.True(PcIdDerivation.IsP256Spki(spki));
                var data = "hello"u8.ToArray();
                var sig = key.SignP1363(data);
                Assert.Equal(64, sig.Length);
                using var pub = ECDsa.Create();
                pub.ImportSubjectPublicKeyInfo(spki, out _);
                Assert.True(pub.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                Assert.ThrowsAny<CryptographicException>(() => key.TryExportPrivateKey());
            }
            using (var reopened = DeviceKeyStore.OpenOrCreate(name, machineKey))
            {
                Assert.False(reopened.Created);
                Assert.Equal(spki, reopened.Spki);
            }
        }
        finally
        {
            DeviceKeyStore.Delete(name, machineKey);
        }
    }
}
