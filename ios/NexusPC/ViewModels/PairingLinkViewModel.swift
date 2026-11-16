import Foundation
import NexusKit
import Observation

/// Reads a pairing link (pasted for now; QR scanning comes in P2) and shows what it points to.
@Observable
final class PairingLinkViewModel {
    var text = ""

    var link: PairingLink? {
        PairingLink(text.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    var hasInput: Bool {
        !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}
