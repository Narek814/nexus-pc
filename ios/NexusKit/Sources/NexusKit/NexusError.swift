import Foundation

public enum NexusError: Error, Equatable, Sendable {
    /// The agent answered with an error (docs/API.md error codes). `details` is the optional
    /// `error.details` object (`feature`, `state`, `limit`, `retryAfter`).
    case server(code: String, message: String, details: ServerErrorDetails? = nil)
    /// No usable connection: unreachable, wrong certificate, closed.
    case connection(String)
    case timeout
    case protocolViolation(String)

    public var code: String? {
        if case let .server(code, _, _) = self {
            return code
        }
        return nil
    }

    public var details: ServerErrorDetails? {
        if case let .server(_, _, details) = self {
            return details
        }
        return nil
    }
}

extension NexusError: LocalizedError {
    /// Fallback text only. The app picks the text by `code` (`ErrorPresentation`); the code itself
    /// is never part of the main text (docs/design/error-texts.md, 1).
    public var errorDescription: String? {
        switch self {
        case let .server(_, message, _):
            return message
        case let .connection(reason):
            return reason
        case .timeout:
            return "ПК не ответил вовремя."
        case let .protocolViolation(reason):
            return "Неожиданный ответ ПК: \(reason)"
        }
    }
}
