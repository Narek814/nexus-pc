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
    /// <summary>
    /// Private addresses of interfaces that have a default gateway (the real home network), plus
    /// Tailscale addresses. This leaves out adapters such as the Windows mobile hotspot and the
    /// WSL/Hyper-V virtual switch, which have no gateway (found on a real PC). If no interface has a
    /// gateway (an offline LAN), every private address is used.
    /// </summary>
    public static IReadOnlyList<IPAddress> EligibleLocalAddresses()
    {
        var withGateway = new List<IPAddress>();
        var allPrivate = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            var properties = nic.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (!IsEligible(address) || allPrivate.Contains(address))
                {
                    continue;
                }
                allPrivate.Add(address);
                if (hasGateway || IsTailscale(address))
                {
                    withGateway.Add(address);
                }
            }
        }
        return withGateway.Count > 0 ? withGateway : allPrivate;
    }

    /// <summary>
    /// Whether a connection that arrived on this local address may be served: loopback, private LAN or
    /// Tailscale. IPv4-mapped IPv6 addresses are unwrapped first.
    /// </summary>
    public static bool IsAllowedLocalAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return IPAddress.IsLoopback(address) || IsEligible(address);
    }

    public static bool IsTailscale(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && b[0] == 100 && b[1] is >= 64 and <= 127;
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
