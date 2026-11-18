import Foundation
import NexusKit

/// Connect + authenticate to the paired PC. Shared by the Command Center and Siri/Shortcuts intents.
enum PCSession {
    /// Tries every known address at once (home Wi-Fi, Tailscale, …) and keeps the first that answers.
    /// If none answers, looks for the PC in its home network: the router may have given it a new address.
    static func connect(_ pc: PairedPC) async throws -> NexusConnection {
        do {
            return try await connectKnown(pc)
        } catch let error as NexusError {
            if case .server = error {
                throw error // the PC answered (e.g. revoked): searching elsewhere will not help
            }
            if let found = await discover(pc) {
                return found
            }
            throw error
        }
    }

    /// Probes the PC's home /24 network for the agent port; a candidate counts only if its certificate
    /// matches the pinned fingerprint and the device signs in. The new address is remembered.
    private static func discover(_ pc: PairedPC) async -> NexusConnection? {
        let candidates = LanDiscovery.candidates(near: pc.hosts)
        guard !candidates.isEmpty else { return nil }
        for host in await LanProbe.open(candidates, port: pc.port) {
            var moved = pc
            moved.hosts = [host]
            guard let connection = try? await connectKnown(moved) else { continue }
            var updated = pc
            updated.hosts = Array(([host] + pc.hosts).prefix(8))
            PairingStore.update(updated)
            return connection
        }
        return nil
    }

    private static func connectKnown(_ pc: PairedPC) async throws -> NexusConnection {
        let signer = try PairingStore.signer(for: pc)
        guard !pc.hosts.isEmpty else {
            throw NexusError.connection("Нет адресов ПК. Подключите ПК заново.")
        }
        return try await withCheckedThrowingContinuation { continuation in
            let race = ConnectionRace(attempts: pc.hosts.count, continuation: continuation)
            for host in pc.hosts {
                Task {
                    do {
                        let (connection, _) = try await NexusConnection.connect(host: host, port: pc.port, fingerprint: pc.fingerprint, timeout: 5)
                        do {
                            try await connection.authenticate(pcId: pc.pcId, deviceId: pc.deviceId, fingerprint: pc.fingerprint, signer: signer)
                        } catch {
                            connection.close()
                            throw error
                        }
                        race.succeed(connection)
                    } catch {
                        race.fail(error)
                    }
                }
            }
        }
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

/// First successful connection wins; later ones are closed. If all fail, the most useful error is reported
/// (an answer from the PC, like auth.failed, beats "no connection").
private final class ConnectionRace: @unchecked Sendable {
    private let lock = NSLock()
    private var remaining: Int
    private var continuation: CheckedContinuation<NexusConnection, Error>?
    private var bestError: Error?

    init(attempts: Int, continuation: CheckedContinuation<NexusConnection, Error>) {
        remaining = attempts
        self.continuation = continuation
    }

    func succeed(_ connection: NexusConnection) {
        lock.lock()
        let winner = continuation
        continuation = nil
        remaining -= 1
        lock.unlock()
        if let winner {
            winner.resume(returning: connection)
        } else {
            connection.close()
        }
    }

    func fail(_ error: Error) {
        lock.lock()
        if bestError == nil || (error as? NexusError)?.code != nil {
            bestError = error
        }
        remaining -= 1
        let done = remaining == 0 ? continuation : nil
        if done != nil {
            continuation = nil
        }
        let reported = bestError
        lock.unlock()
        done?.resume(throwing: reported ?? NexusError.connection("Нет связи с ПК."))
    }
}
