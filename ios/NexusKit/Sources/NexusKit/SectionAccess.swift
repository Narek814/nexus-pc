import Foundation

/// What a section that needs the PC shows instead of (or as) its content.
/// Order: docs/design/ios-screens.md, section 2, steps 1–6. Pure, unit-tested.
public enum SectionAccess: Equatable, Sendable {
    public enum Connection: Equatable, Sendable {
        case online
        case connecting
        case offline
    }

    case noPC
    case offline
    case connecting
    /// The iPhone has no permission for this section; the app maps it to its `PermissionName`.
    case permission
    case trayMissing
    /// Pro feature without the plan; `state` is the hello's `entitlementState` (expired/revoked change the text).
    case pro(EntitlementState?)
    case content

    /// - Parameters:
    ///   - commands: the section is available if the PC allows at least one of these commands.
    ///   - needsTray: the section needs the tray app (`pc.status.userSession == available`).
    ///   - requiredFeature: Pro feature id (docs/API.md, «Планы»); `nil` for Free sections.
    public static func resolve(isPaired: Bool, connection: Connection, capabilities: Set<String>,
                               anyOf commands: [String], userSessionAvailable: Bool, needsTray: Bool,
                               hello: AgentHello?, requiredFeature: String?) -> SectionAccess {
        // 1
        if !isPaired {
            return .noPC
        }
        // 2 and 3: owner's decision — any lost connection is offline, whatever capabilities are cached.
        switch connection {
        case .offline:
            return .offline
        case .connecting:
            return .connecting
        case .online:
            break
        }
        // 4: permission always before Pro, as on the agent.
        if !commands.contains(where: { capabilities.contains($0) }) {
            return .permission
        }
        // 5
        if needsTray, !userSessionAvailable {
            return .trayMissing
        }
        // 6: only agents with the entitlements.v1 marker can say "no plan".
        if let feature = requiredFeature, let hello, hello.supportsEntitlements, !hello.features.contains(feature) {
            return .pro(hello.entitlementState)
        }
        return .content
    }
}
