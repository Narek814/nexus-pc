import ActivityKit
import Foundation

/// Live Activity / Dynamic Island for a scheduled shutdown or restart. The countdown is rendered by
/// the system from `endsAt`, so it needs no updates (and no push server).
struct PowerCountdownAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        var endsAt: Date
    }

    var pcName: String
    /// "shutdown" or "restart"
    var action: String
}

/// URLs the widget and pairing QR codes open. Actions from links never shut down without confirmation.
enum DeepLink {
    static let lock = URL(string: "nexuspc://action/lock")!
    static let shutdown = URL(string: "nexuspc://action/shutdown")!
    static let cancel = URL(string: "nexuspc://action/cancel")!
    static let open = URL(string: "nexuspc://action/open")!

    static func profile(_ id: String) -> URL {
        URL(string: "nexuspc://action/profile/\(id)")!
    }
}
