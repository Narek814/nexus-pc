import Foundation

/// The agent's plan state (docs/API.md, «Планы»). Values this version does not know stay as `.unknown`.
public enum EntitlementState: Equatable, Hashable, Sendable {
    case none
    case active
    case grace
    case expired
    case revoked
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "none": self = .none
        case "active": self = .active
        case "grace": self = .grace
        case "expired": self = .expired
        case "revoked": self = .revoked
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .none: "none"
        case .active: "active"
        case .grace: "grace"
        case .expired: "expired"
        case .revoked: "revoked"
        case let .unknown(value): value
        }
    }
}

/// Result of `system.hello` (docs/API.md, «Каталог команд v1» and «Планы»). Lenient: unknown
/// fields are ignored, missing or mistyped fields fall back to empty values, unknown plan and
/// state values are kept as strings.
public struct AgentHello: Equatable, Sendable {
    public let protocolVersion: Int
    public let pcId: String
    public let pcName: String
    public let agentVersion: String?
    public let authenticated: Bool
    public let capabilities: Set<String>
    /// `free` or `pro` (any other value is kept as is). Only after `auth.prove` on agents with plans.
    public let plan: String?
    public let features: Set<String>
    public let entitlementState: EntitlementState?
    public let validUntil: Date?

    public init(result: [String: Any]) {
        protocolVersion = Self.int(result["protocol"]) ?? 0
        pcId = result["pcId"] as? String ?? ""
        pcName = result["pcName"] as? String ?? ""
        agentVersion = result["agentVersion"] as? String
        authenticated = result["authenticated"] as? Bool ?? false
        capabilities = Set((result["capabilities"] as? [Any] ?? []).compactMap { $0 as? String })
        plan = result["plan"] as? String
        features = Set((result["features"] as? [Any] ?? []).compactMap { $0 as? String })
        entitlementState = (result["entitlementState"] as? String).map(EntitlementState.init(rawValue:))
        validUntil = (result["validUntil"] as? String).flatMap(Self.parseDate)
    }

    /// The agent understands plan fields, `account.refresh` and `plan.*` errors.
    public var supportsEntitlements: Bool {
        capabilities.contains("entitlements.v1")
    }

    static func int(_ value: Any?) -> Int? {
        if let value = value as? Int {
            return value
        }
        return (value as? NSNumber)?.intValue
    }

    static func parseDate(_ text: String) -> Date? {
        let plain = ISO8601DateFormatter()
        if let date = plain.date(from: text) {
            return date
        }
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return fractional.date(from: text)
    }
}

/// `error.details` of an error response (docs/API.md, «Ответы»). Other fields are ignored.
public struct ServerErrorDetails: Equatable, Sendable {
    public var feature: String?
    public var state: String?
    public var limit: Int64?
    public var retryAfter: Int?

    public init(feature: String? = nil, state: String? = nil, limit: Int64? = nil, retryAfter: Int? = nil) {
        self.feature = feature
        self.state = state
        self.limit = limit
        self.retryAfter = retryAfter
    }

    /// `nil` if `object` is not a JSON object or has none of the known fields.
    public init?(json object: Any?) {
        guard let dict = object as? [String: Any] else {
            return nil
        }
        feature = dict["feature"] as? String
        state = dict["state"] as? String
        limit = AgentHello.int(dict["limit"]).map(Int64.init)
        retryAfter = AgentHello.int(dict["retryAfter"])
        if feature == nil, state == nil, limit == nil, retryAfter == nil {
            return nil
        }
    }
}
