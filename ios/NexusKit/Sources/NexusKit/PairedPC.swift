import Foundation

/// What the phone remembers about a paired PC. Not secret except as a whole; stored in the Keychain.
public struct PairedPC: Codable, Equatable, Identifiable, Sendable {
    public var id: UUID { pcId }
    public let pcId: UUID
    public var name: String
    public var hosts: [String]
    public var port: Int
    public let fingerprint: String
    public let deviceId: UUID
    public var permissions: [String]

    public init(link: PairingLink, deviceId: UUID, permissions: [String]) {
        pcId = link.pcId
        name = link.pcName
        hosts = link.hosts
        port = link.port
        fingerprint = link.certificateFingerprint
        self.deviceId = deviceId
        self.permissions = permissions
    }
}
