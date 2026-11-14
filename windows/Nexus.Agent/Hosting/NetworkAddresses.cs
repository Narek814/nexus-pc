using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Nexus.Agent.Hosting;

/// <summary>
/// Picks the addresses the agent listens on: private LAN ranges and the Tailscale/CGNAT range
/// only. Never 0.0.0.0 or a public address (SECURITY.md §3.7).
/// </summary>
internal static class NetworkAddresses
{
    public static IReadOnlyList<IPAddress> EligibleLocalAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (IsEligible(unicast.Address) && !result.Contains(unicast.Address))
                {
                    result.Add(unicast.Address);
                }
            }
        }
        return result;
    }

    /// <summary>RFC 1918 private ranges and 100.64.0.0/10 (Tailscale). IPv4 only for now.</summary>
    public static bool IsEligible(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
