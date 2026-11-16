import Foundation

public enum NexusError: Error, Equatable, Sendable {
    /// The agent answered with an error (docs/API.md error codes).
    case server(code: String, message: String)
    /// No usable connection: unreachable, wrong certificate, closed.
    case connection(String)
    case timeout
    case protocolViolation(String)

    public var code: String? {
        if case let .server(code, _) = self {
            return code
        }
        return nil
    }
}

extension NexusError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case let .server(code, message):
            return "\(message) (\(code))"
        case let .connection(reason):
            return reason
        case .timeout:
            return "ПК не ответил вовремя."
        case let .protocolViolation(reason):
            return "Неожиданный ответ ПК: \(reason)"
        }
    }
}
