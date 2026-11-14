using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Core.Pairing;

/// <summary>
/// Contents of the pairing QR code: nexuspc://pair?v=1&amp;pc=…&amp;name=…&amp;h=…&amp;p=…&amp;fp=…&amp;c=…
/// Everything a phone needs to connect and pin the agent's certificate on first contact.
/// </summary>
public sealed partial record PairingLink(Guid PcId, string PcName, IReadOnlyList<string> Hosts, int Port, string CertificateFingerprint, string Code)
{
    private const string Prefix = "nexuspc://pair?";

    public override string ToString()
    {
        var query = new StringBuilder(Prefix);
        query.Append("v=1");
        query.Append("&pc=").Append(PcId.ToString("D"));
        query.Append("&name=").Append(Uri.EscapeDataString(PcName));
        query.Append("&h=").Append(Uri.EscapeDataString(string.Join(',', Hosts)));
        query.Append("&p=").Append(Port.ToString(CultureInfo.InvariantCulture));
        query.Append("&fp=").Append(CertificateFingerprint);
        query.Append("&c=").Append(Code);
        return query.ToString();
    }

    public static bool TryParse(string? text, out PairingLink link)
    {
        link = null!;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal) || text.Length > 2048)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text[Prefix.Length..].Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !values.TryAdd(pair[..eq], Uri.UnescapeDataString(pair[(eq + 1)..])))
            {
                return false;
            }
        }

        if (!values.TryGetValue("v", out var v) || v != "1"
            || !values.TryGetValue("pc", out var pc) || !Guid.TryParseExact(pc, "D", out var pcId)
            || !values.TryGetValue("name", out var name) || name.Length is 0 or > 64
            || !values.TryGetValue("h", out var hostsText)
            || !values.TryGetValue("p", out var portText) || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535
            || !values.TryGetValue("fp", out var fp) || !Fingerprint().IsMatch(fp)
            || !values.TryGetValue("c", out var code) || !PairingCode().IsMatch(code))
        {
            return false;
        }

        var hosts = hostsText.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Length is 0 or > 8 || hosts.Any(h => !IPAddress.TryParse(h, out _)))
        {
            return false;
        }

        link = new PairingLink(pcId, name, hosts, port, fp, code);
        return true;
    }

    public static bool IsValidCode(string? code) => code is not null && PairingCode().IsMatch(code);

    [GeneratedRegex(@"^[0-9A-F]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Fingerprint();

    [GeneratedRegex(@"^[0-9]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PairingCode();
}
