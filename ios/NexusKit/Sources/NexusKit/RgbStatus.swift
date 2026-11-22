import Foundation

/// Availability part of the `rgb.status` result. `state` is optional: old agents never send it,
/// newer agents send `"conflict"` (with `available: false`) when another PC program has taken over the lighting.
public struct RgbStatus: Decodable, Equatable, Sendable {
    public let available: Bool
    public let reason: String?
    public let state: String?

    public init(available: Bool, reason: String? = nil, state: String? = nil) {
        self.available = available
        self.reason = reason
        self.state = state
    }

    /// Reads the fields from a decoded JSON result; missing or mistyped fields fall back to the old defaults.
    public init(result: [String: Any]) {
        available = result["available"] as? Bool ?? false
        reason = result["reason"] as? String
        state = result["state"] as? String
    }

    /// Lighting is held by another program on the PC; controls must not be shown.
    public var isConflict: Bool { state == "conflict" }
}
