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

    /// <summary>
    /// Where a Wake-on-LAN packet for this PC should go: the MAC and subnet broadcast of each physical
    /// interface that has a gateway (Tailscale and virtual adapters are skipped: they cannot wake the PC).
    /// </summary>
    public static IReadOnlyList<(string Interface, string Mac, string Broadcast, bool Wireless)> WakeTargets()
    {
        var targets = new List<(string, string, string, bool)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            var properties = nic.GetIPProperties();
            if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            {
                continue;
            }
            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length != 6)
            {
                continue;
            }
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IsTailscale(unicast.Address) || !IsEligible(unicast.Address))
                {
                    continue;
                }
                targets.Add((nic.Name, string.Join(':', mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))),
                    BroadcastOf(unicast.Address, unicast.IPv4Mask).ToString(), nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211));
            }
        }
        return targets;
    }

    public static IPAddress BroadcastOf(IPAddress address, IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(mask);
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(ip[i] | ~m[i]);
        }
        return new IPAddress(broadcast);
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
