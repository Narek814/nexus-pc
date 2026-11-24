using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Agent.Account;

/// <summary>
/// Strings the PC signs for the account server (docs/API.md, "Сервер прав"); the server rebuilds them itself from
/// the request fields. ASCII, pcId lower-case "D", sha256hex lower-case, ts in Unix seconds.
/// </summary>
internal static class PcLoginStrings
{
    public const string Prefix = "nexus-pc-login-v1";
    public const int NonceBytes = 32; // 16..32 allowed; a fresh one for every request and every retry

    public static string Start(Guid pcId, string spkiB64u, string name, string nonceB64u, long ts) =>
        $"{Prefix}|start|{Id(pcId)}|{spkiB64u}|{Sha256Hex(name)}|{nonceB64u}|{Ts(ts)}";

    public static string Consume(Guid pcId, string deviceCode, string nonceB64u, long ts) =>
        $"{Prefix}|consume|{Id(pcId)}|{Sha256Hex(deviceCode)}|{nonceB64u}|{Ts(ts)}";

    public static string Refresh(Guid pcId, string refreshToken, string nonceB64u, long ts) =>
        $"{Prefix}|refresh|{Id(pcId)}|{Sha256Hex(refreshToken)}|{nonceB64u}|{Ts(ts)}";

    public static string Id(Guid pcId) => pcId.ToString("D", CultureInfo.InvariantCulture);

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string NewNonce() => B64u(RandomNumberGenerator.GetBytes(NonceBytes));

    public static string B64u(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Ts(long ts) => ts.ToString(CultureInfo.InvariantCulture);
}
