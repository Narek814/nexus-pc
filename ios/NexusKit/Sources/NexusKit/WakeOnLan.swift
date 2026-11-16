import Foundation

/// Where the PC can be woken: reported by the agent in pc.status while it is on, remembered by the phone.
public struct WakeTarget: Codable, Equatable, Sendable {
    public let mac: String
    public let broadcast: String
    public let wireless: Bool

    public init(mac: String, broadcast: String, wireless: Bool) {
        self.mac = mac
        self.broadcast = broadcast
        self.wireless = wireless
    }
}

public enum WakeOnLan {
    /// The standard "magic packet": 6 × 0xFF followed by the MAC address 16 times (102 bytes).
    public static func magicPacket(mac: String) -> Data? {
        let parts = mac.split(whereSeparator: { $0 == ":" || $0 == "-" })
        let bytes = parts.compactMap { UInt8($0, radix: 16) }
        guard parts.count == 6, bytes.count == 6 else {
            return nil
        }
        var packet = Data(repeating: 0xFF, count: 6)
        for _ in 0..<16 {
            packet.append(contentsOf: bytes)
        }
        return packet
    }
}
