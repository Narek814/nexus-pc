import CryptoKit
import Foundation
import NexusKit

/// This phone's identity key, one per paired PC. Secure Enclave when available (all modern iPhones):
/// the private key never leaves the chip, the Keychain only holds an encrypted handle to it.
enum DeviceIdentity {
    static func create() throws -> (signer: DeviceSigner, stored: Data) {
        if SecureEnclaveSigner.isAvailable {
            let key = try SecureEnclaveSigner()
            return (key, Data("se:".utf8) + key.dataRepresentation)
        }
        let key = SoftwareSigner()
        return (key, Data("sw:".utf8) + key.rawRepresentation)
    }

    static func restore(_ stored: Data) throws -> DeviceSigner {
        let tag = String(decoding: stored.prefix(3), as: UTF8.self)
        let body = stored.dropFirst(3)
        switch tag {
        case "se:":
            return try SecureEnclaveSigner(dataRepresentation: Data(body))
        case "sw:":
            return SoftwareSigner(key: try P256.Signing.PrivateKey(rawRepresentation: Data(body)))
        default:
            throw NexusError.protocolViolation("unknown key format")
        }
    }
}

/// Paired PCs and their keys in the Keychain. Multi-PC ready (P8); the UI shows the first one for now.
enum PairingStore {
    private static let listAccount = "paired-pcs"

    static func load() -> [PairedPC] {
        guard let data = KeychainStore.load(account: listAccount) else {
            return []
        }
        return (try? JSONDecoder().decode([PairedPC].self, from: data)) ?? []
    }

    static func save(_ pc: PairedPC, key: Data) throws {
        try KeychainStore.save(key, account: "key-\(pc.pcId.uuidString)")
        var all = load().filter { $0.pcId != pc.pcId }
        all.append(pc)
        try KeychainStore.save(try JSONEncoder().encode(all), account: listAccount)
    }

    /// Updates what the phone knows about a PC (addresses, wake targets) without touching its key.
    static func update(_ pc: PairedPC) {
        var all = load()
        guard let index = all.firstIndex(where: { $0.pcId == pc.pcId }) else {
            return
        }
        all[index] = pc
        if let data = try? JSONEncoder().encode(all) {
            try? KeychainStore.save(data, account: listAccount)
        }
    }

    static func signer(for pc: PairedPC) throws -> DeviceSigner {
        guard let stored = KeychainStore.load(account: "key-\(pc.pcId.uuidString)") else {
            throw NexusError.connection("Ключ для этого ПК не найден. Подключите ПК заново.")
        }
        return try DeviceIdentity.restore(stored)
    }

    static func forget(_ pc: PairedPC) {
        KeychainStore.delete(account: "key-\(pc.pcId.uuidString)")
        let remaining = load().filter { $0.pcId != pc.pcId }
        if let data = try? JSONEncoder().encode(remaining) {
            try? KeychainStore.save(data, account: listAccount)
        }
    }
}
