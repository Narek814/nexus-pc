using System.Security.Cryptography;
using System.Text.Json;

namespace Nexus.DevClient;

/// <summary>
/// Development-only identity: the private key is stored UNENCRYPTED in a file. The iPhone app keeps
/// its key in the Secure Enclave instead (SECURITY.md §3.3). Do not use this file outside testing.
/// </summary>
internal sealed record DeviceIdentityFile(Guid PcId, string PcName, string[] Hosts, int Port, string Fingerprint, Guid DeviceId, string PrivateKeyPkcs8)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus-dev", "device.json");

    public ECDsa LoadKey()
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKeyPkcs8), out _);
        return key;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static DeviceIdentityFile Load(string path) =>
        JsonSerializer.Deserialize<DeviceIdentityFile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty.");
}
