import Foundation
import Network

/// Is anything listening on host:port? A plain TCP connect with a short timeout; nothing is sent.
enum LanProbe {
    static func isOpen(_ host: String, port: Int, timeout: TimeInterval = 0.6) async -> Bool {
        guard let nwPort = NWEndpoint.Port(rawValue: UInt16(port)) else { return false }
        let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .tcp)
        return await withCheckedContinuation { continuation in
            let once = Once(continuation)
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    once.resume(true)
                    connection.cancel()
                case .failed, .cancelled:
                    once.resume(false)
                case .waiting:
                    once.resume(false)
                    connection.cancel()
                default:
                    break
                }
            }
            connection.start(queue: .global())
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                once.resume(false)
                connection.cancel()
            }
        }
    }

    /// Probes candidates in parallel batches; returns the open ones in the order found.
    static func open(_ hosts: [String], port: Int, batch: Int = 48) async -> [String] {
        var found: [String] = []
        var index = 0
        while index < hosts.count {
            let slice = Array(hosts[index..<min(index + batch, hosts.count)])
            index += batch
            let results = await withTaskGroup(of: (String, Bool).self) { group in
                for host in slice {
                    group.addTask { (host, await isOpen(host, port: port)) }
                }
                var opened: [String] = []
                for await (host, open) in group where open {
                    opened.append(host)
                }
                return opened
            }
            found += results
            if !found.isEmpty {
                return found // nearest batch with an answer is enough
            }
        }
        return found
    }

    private final class Once: @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: CheckedContinuation<Bool, Never>?

        init(_ continuation: CheckedContinuation<Bool, Never>) {
            self.continuation = continuation
        }

        func resume(_ value: Bool) {
            lock.lock()
            let c = continuation
            continuation = nil
            lock.unlock()
            c?.resume(returning: value)
        }
    }
}
