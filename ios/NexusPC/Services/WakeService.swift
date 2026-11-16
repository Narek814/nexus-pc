import Foundation
import Network
import NexusKit

/// Sends Wake-on-LAN magic packets from the iPhone (UDP 9 and 7) to the PC's last LAN addresses
/// (unicast) and its subnet broadcast. Works only on the same network as the PC, and only if the PC's
/// network card and BIOS allow waking (usually wired Ethernet). iOS may refuse the broadcast without a
/// special Apple entitlement; the unicast packets are still sent.
enum WakeService {
    /// Number of packets the system accepted for sending (not proof that the PC woke up).
    static func send(to pc: PairedPC) async -> Int {
        guard let targets = pc.wake, !targets.isEmpty else {
            return 0
        }
        let lanHosts = pc.hosts.filter { !$0.hasPrefix("100.") }
        var sent = 0
        for target in targets {
            guard let packet = WakeOnLan.magicPacket(mac: target.mac) else {
                continue
            }
            for host in Set(lanHosts + [target.broadcast]) {
                for port in [UInt16(9), UInt16(7)] {
                    if await sendDatagram(packet, to: host, port: port) {
                        sent += 1
                    }
                }
            }
        }
        return sent
    }

    private static func sendDatagram(_ data: Data, to host: String, port: UInt16) async -> Bool {
        guard let nwPort = NWEndpoint.Port(rawValue: port) else {
            return false
        }
        let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .udp)
        return await withCheckedContinuation { continuation in
            let once = Once()
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    connection.send(content: data, completion: .contentProcessed { error in
                        once.run { continuation.resume(returning: error == nil) }
                        connection.cancel()
                    })
                case .failed, .cancelled:
                    once.run { continuation.resume(returning: false) }
                default:
                    break
                }
            }
            connection.start(queue: .global())
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) {
                once.run { continuation.resume(returning: false) }
                connection.cancel()
            }
        }
    }
}

private final class Once: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false

    func run(_ body: () -> Void) {
        lock.lock()
        defer { lock.unlock() }
        guard !done else { return }
        done = true
        body()
    }
}
