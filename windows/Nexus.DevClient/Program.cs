using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Pairing;
using Nexus.Core.Security;

namespace Nexus.DevClient;

internal static class Program
{
    private const string Usage = """
        Nexus.DevClient — test client for Nexus.Agent (stands in for the iPhone app until P2)

          Nexus.DevClient pair "<nexuspc://pair?... link>" [--name NAME] [--host IP]
          Nexus.DevClient send <type> [payload-json] [--confirm] [--delay SECONDS] [--force]
                                    [--app APP_ID] [--profile PROFILE_ID] [--host IP]
          Nexus.DevClient hello [--host IP]

        Options: --identity PATH (default ~/.nexus-dev/device.json)
        Examples:
          Nexus.DevClient send pc.status
          Nexus.DevClient send power.shutdown --delay 600 --confirm
          Nexus.DevClient send power.cancel
          Nexus.DevClient send apps.launch --app steam
          Nexus.DevClient send profiles.run --profile gaming
        WARNING: the identity file holds an unencrypted private key. Testing only.
        """;

    // Readable console output: keep Cyrillic as is (display only).
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        try
        {
            return options.Positional switch
            {
                ["pair", var link] => await PairAsync(link, options).ConfigureAwait(false),
                ["send", var type] => await SendAsync(type, null, options).ConfigureAwait(false),
                ["send", var type, var payload] => await SendAsync(type, payload, options).ConfigureAwait(false),
                ["hello"] => await HelloAsync(options).ConfigureAwait(false),
                ["qr-selftest", var output] => QrSelfTest(output),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or JsonException or FileNotFoundException or InvalidDataException)
        {
            await Console.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 1;
    }

    private static async Task<int> PairAsync(string linkText, Options options)
    {
        if (!PairingLink.TryParse(linkText, out var link))
        {
            await Console.Error.WriteLineAsync("Not a valid nexuspc://pair link.").ConfigureAwait(false);
            return 1;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = options.Name ?? $"DevClient on {Environment.MachineName}";
        var host = options.Host ?? link.Hosts[0];

        Console.WriteLine($"Connecting to {link.PcName} at {host}:{link.Port}...");
        Console.WriteLine($"Verification code: {DeviceCrypto.ShortAuthenticationString(key.ExportSubjectPublicKeyInfo(), link.CertificateFingerprint)}  (compare with the PC, then approve there)");

        await using var client = await NexusClient.ConnectAsync(host, link.Port, link.CertificateFingerprint, CancellationToken.None).ConfigureAwait(false);
        var response = await client.PairAsync(link.Code, name, key).ConfigureAwait(false);
        Console.WriteLine(response.ToJsonString(Pretty));
        if (response["ok"]?.GetValue<bool>() != true)
        {
            return 1;
        }

        var identity = new DeviceIdentityFile(link.PcId, link.PcName, [.. link.Hosts], link.Port, link.CertificateFingerprint,
            Guid.Parse(response["payload"]!["deviceId"]!.GetValue<string>()), Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        identity.Save(options.IdentityPath);
        Console.WriteLine($"Saved identity to {options.IdentityPath} (unencrypted, testing only).");
        return 0;
    }

    private static async Task<int> SendAsync(string type, string? payloadJson, Options options)
    {
        var identity = DeviceIdentityFile.Load(options.IdentityPath);
        using var key = identity.LoadKey();
        await using var client = await NexusClient.ConnectAsync(options.Host ?? identity.Hosts[0], identity.Port, identity.Fingerprint, CancellationToken.None).ConfigureAwait(false);

        var auth = await client.AuthenticateAsync(identity.PcId, identity.DeviceId, key, identity.Fingerprint).ConfigureAwait(false);
        if (auth["ok"]?.GetValue<bool>() != true)
        {
            Console.WriteLine(auth.ToJsonString(Pretty));
            return 1;
        }

        var payload = payloadJson is null ? null : JsonNode.Parse(payloadJson)?.AsObject();
        if (options.Delay is { } delay)
        {
            payload ??= [];
            payload["delaySeconds"] = delay;
        }
        if (options.Force)
        {
            payload ??= [];
            payload["force"] = true;
        }
        if (options.App is { } app)
        {
            payload ??= [];
            payload["appId"] = app;
        }
        if (options.ProfileId is { } profile)
        {
            payload ??= [];
            payload["profileId"] = profile;
        }
        var response = await client.RequestAsync(type, payload, options.Confirm).ConfigureAwait(false);
        Console.WriteLine(response.ToJsonString(Pretty));
        return response["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    private static async Task<int> HelloAsync(Options options)
    {
        var identity = DeviceIdentityFile.Load(options.IdentityPath);
        await using var client = await NexusClient.ConnectAsync(options.Host ?? identity.Hosts[0], identity.Port, identity.Fingerprint, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine((await client.RequestAsync("system.hello").ConfigureAwait(false)).ToJsonString(Pretty));
        return 0;
    }

    /// <summary>
    /// Writes QR codes for payloads covering every version 1–40 (plus real pairing links and Cyrillic)
    /// so tools/check_qr.py can decode them with an independent decoder.
    /// </summary>
    private static int QrSelfTest(string outputPath)
    {
        var texts = new List<string>
        {
            "nexuspc://pair?v=1&pc=42fe00f7-98ad-4607-84c0-4580b73d2f0e&name=PC-NAREK&h=192.168.0.106&p=47800&fp=711B91EC8E18D225D4870FAC3855C8B66B5EF15F9ABA09910A2810F1C557A190&c=57930317",
            new PairingLink(Guid.NewGuid(), "Игровой ПК", ["192.168.0.106", "100.101.102.103"], 47800, new string('A', 64), "01234567").ToString(),
            "a",
        };
        var random = new Random(1234);
        int lastVersion = 0;
        for (var length = 1; length <= 2331; length = length == 2331 ? 2332 : Math.Min(2331, length + 1 + length / 40))
        {
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = (char)random.Next(0x21, 0x7F);
            }
            var text = new string(chars);
            var version = QrCode.EncodeText(text).Version;
            if (version != lastVersion)
            {
                texts.Add(text);
                lastVersion = version;
            }
        }
        var cases = texts.Select(t =>
        {
            var qr = QrCode.EncodeText(t);
            return new { text = t, version = qr.Version, mask = qr.Mask, rows = qr.ToRows() };
        }).ToList();
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new { cases }));
        Console.WriteLine($"Wrote {cases.Count} QR codes, versions {cases.Min(c => c.version)}–{cases.Max(c => c.version)}.");
        return 0;
    }

    private sealed record Options(string[] Positional, string? Name, string? Host, bool Confirm, string IdentityPath, int? Delay, bool Force, string? App, string? ProfileId)
    {
        public static Options Parse(string[] args)
        {
            var positional = new List<string>();
            string? name = null, host = null, identity = null, app = null, profile = null;
            var confirm = false;
            var force = false;
            int? delay = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--name" when i + 1 < args.Length: name = args[++i]; break;
                    case "--host" when i + 1 < args.Length: host = args[++i]; break;
                    case "--identity" when i + 1 < args.Length: identity = args[++i]; break;
                    case "--app" when i + 1 < args.Length: app = args[++i]; break;
                    case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
                    case "--confirm": confirm = true; break;
                    case "--force": force = true; break;
                    case "--delay" when i + 1 < args.Length && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds):
                        delay = seconds;
                        i++;
                        break;
                    default: positional.Add(args[i]); break;
                }
            }
            return new Options([.. positional], name, host, confirm, identity ?? DeviceIdentityFile.DefaultPath, delay, force, app, profile);
        }
    }
}
