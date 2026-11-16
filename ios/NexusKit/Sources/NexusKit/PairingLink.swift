import Foundation

/// Contents of the pairing QR code shown by Nexus.Tray:
/// `nexuspc://pair?v=1&pc=…&name=…&h=…&p=…&fp=…&c=…`.
/// Must accept and reject exactly what C# `Nexus.Core.Pairing.PairingLink.TryParse` does
/// (checked by shared/protocol/fixtures/pairing-links.v1.json on both sides).
public struct PairingLink: Equatable, Sendable {
    public let pcId: UUID
    public let pcName: String
    public let hosts: [String]
    public let port: Int
    /// SHA-256 of the agent's TLS certificate, uppercase hex. Pinned on first connection.
    public let certificateFingerprint: String
    public let code: String

    private static let prefix = "nexuspc://pair?"

    public init?(_ text: String) {
        guard text.hasPrefix(Self.prefix), text.utf16.count <= 2048 else {
            return nil
        }
        var values: [String: String] = [:]
        let query = text.dropFirst(Self.prefix.count)
        for pair in query.split(separator: "&", omittingEmptySubsequences: false) {
            guard let eq = pair.firstIndex(of: "="), eq != pair.startIndex else {
                return nil
            }
            let key = String(pair[..<eq])
            guard let value = String(pair[pair.index(after: eq)...]).removingPercentEncoding, values[key] == nil else {
                return nil
            }
            values[key] = value
        }

        guard values["v"] == "1",
              let pcText = values["pc"], let pcId = UUID(uuidString: pcText),
              let name = values["name"], (1...64).contains(name.utf16.count),
              let hostsText = values["h"],
              let portText = values["p"], Self.isDigits(portText), let port = Int(portText), (1...65535).contains(port),
              let fingerprint = values["fp"], fingerprint.utf8.count == 64, fingerprint.utf8.allSatisfy(Self.isUpperHex),
              let code = values["c"], code.utf8.count == 8, Self.isDigits(code)
        else {
            return nil
        }

        let hosts = hostsText.split(separator: ",").map(String.init)
        guard (1...8).contains(hosts.count), hosts.allSatisfy(IPAddressText.isValid) else {
            return nil
        }

        self.pcId = pcId
        self.pcName = name
        self.hosts = hosts
        self.port = port
        self.certificateFingerprint = fingerprint
        self.code = code
    }

    private static func isDigits(_ text: String) -> Bool {
        !text.isEmpty && text.utf8.allSatisfy { $0 >= 0x30 && $0 <= 0x39 }
    }

    private static func isUpperHex(_ byte: UInt8) -> Bool {
        (byte >= 0x30 && byte <= 0x39) || (byte >= 0x41 && byte <= 0x46)
    }
}

/// Strict IPv4 (dotted quad) or IPv6 literal. Host names are never accepted in a pairing link.
enum IPAddressText {
    static func isValid(_ text: String) -> Bool {
        var v4 = in_addr()
        var v6 = in6_addr()
        return text.withCString { inet_pton(AF_INET, $0, &v4) } == 1
            || text.withCString { inet_pton(AF_INET6, $0, &v6) } == 1
    }
}
