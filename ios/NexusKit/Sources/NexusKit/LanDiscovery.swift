import Foundation

/// Finding the PC again after the router gave it a new address (DHCP): only the private /24 networks
/// the PC was seen in, nearest addresses first. A found PC is still checked by its pinned certificate.
public enum LanDiscovery {
    /// Addresses to probe, in order: same /24 as each known private IPv4 address, closest first, known ones excluded.
    public static func candidates(near hosts: [String]) -> [String] {
        var seen = Set(hosts)
        var result: [String] = []
        for host in hosts {
            let parts = host.split(separator: ".").compactMap { Int($0) }
            guard parts.count == 4, parts.allSatisfy({ (0...255).contains($0) }), isPrivate(parts) else { continue }
            let prefix = "\(parts[0]).\(parts[1]).\(parts[2])."
            let known = parts[3]
            let order = (1...254).sorted { abs($0 - known) == abs($1 - known) ? $0 < $1 : abs($0 - known) < abs($1 - known) }
            for last in order {
                let candidate = prefix + String(last)
                if seen.insert(candidate).inserted {
                    result.append(candidate)
                }
            }
        }
        return result
    }

    /// 10/8, 172.16/12, 192.168/16 only: never scan beyond the home network.
    static func isPrivate(_ p: [Int]) -> Bool {
        p[0] == 10 || (p[0] == 172 && (16...31).contains(p[1])) || (p[0] == 192 && p[1] == 168)
    }
}
