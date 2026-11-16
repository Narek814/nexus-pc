import Foundation
import NexusKit

/// Connect + authenticate to the paired PC. Shared by the Command Center and Siri/Shortcuts intents.
enum PCSession {
    static func connect(_ pc: PairedPC) async throws -> NexusConnection {
        let signer = try PairingStore.signer(for: pc)
        var lastError: Error = NexusError.connection("Нет адресов ПК.")
        for host in pc.hosts {
            do {
                let (connection, _) = try await NexusConnection.connect(host: host, port: pc.port, fingerprint: pc.fingerprint, timeout: 4)
                do {
                    try await connection.authenticate(pcId: pc.pcId, deviceId: pc.deviceId, fingerprint: pc.fingerprint, signer: signer)
                    return connection
                } catch {
                    connection.close()
                    throw error
                }
            } catch {
                lastError = error
            }
        }
        throw lastError
    }

    /// One-shot use for intents: connect, run, close.
    static func withPC<T>(_ body: (PairedPC, NexusConnection) async throws -> T) async throws -> T {
        guard let pc = PairingStore.load().first else {
            throw NexusError.connection("Nexus PC is not paired with a PC yet. Open the app to pair.")
        }
        let connection = try await connect(pc)
        defer { connection.close() }
        return try await body(pc, connection)
    }
}
