import Foundation

/// How the app shows an agent error: a String Catalog key and a component, chosen by
/// `error.code` (docs/design/error-texts.md, sections 2 and 3). No SwiftUI here, so it is unit-tested.
public struct ErrorPresentation: Equatable, Sendable {
    public enum Style: String, Equatable, Sendable {
        /// `ErrorCard` instead of the section content.
        case card
        /// Error toast; the content stays.
        case toast
        /// Text under the field, button or transfer row.
        case inline
        /// `PermissionCard` with the section's permission name.
        case permission
        /// `ProCard`.
        case pro
        /// `LimitNotice`.
        case limit
        /// `OfflineCard`.
        case offline
        /// `TrayMissingCard`.
        case tray
        /// Not shown: the app recovers by itself (reconnect, JPEG frames, restart sharing).
        case silent
    }

    /// String Catalog key; `nil` for an unknown code (see `fallbackMessage`) and for some silent codes.
    public let key: String?
    public let style: Style

    public init(key: String?, style: Style) {
        self.key = key
        self.style = style
    }

    /// Sections for `section`: only `control` and `files` change the text today (`session.unavailable`).
    public static func `for`(code: String, details: ServerErrorDetails?, section: String?) -> ErrorPresentation {
        switch code {
        // 3.1 Envelope and access checks
        case "protocol.tooLarge": return .init(key: "error.protocol.tooLarge", style: .toast)
        case "protocol.malformed": return .init(key: "error.protocol.malformed", style: .card)
        case "protocol.unsupportedVersion": return .init(key: "error.protocol.unsupportedVersion", style: .card)
        case "command.unknown": return .init(key: "error.command.unknown", style: .card)
        case "command.notImplemented": return .init(key: "error.command.notImplemented", style: .card)
        case "command.invalidPayload": return .init(key: "error.command.invalidPayload", style: .toast)
        case "auth.required": return .init(key: "error.auth.required", style: .silent) // reconnect; on failure auth.failed
        case "auth.failed": return .init(key: "error.auth.failed", style: .card)
        case "auth.permissionDenied": return .init(key: "permission.title", style: .permission)
        case "auth.clockSkew": return .init(key: "error.auth.clockSkew", style: .card)
        case "auth.replay": return .init(key: "error.auth.replay", style: .toast)
        // The action's own confirmationDialog comes first; the toast is only for actions without one.
        case "confirm.required", "auth.confirmationRequired": return .init(key: "error.confirm.required", style: .toast)

        // 3.2 Plan
        case "plan.required":
            switch details?.state {
            case "expired": return .init(key: "pro.expired.body", style: .pro)
            case "revoked": return .init(key: "pro.revoked.body", style: .pro)
            default:
                if let feature = details?.feature, !feature.isEmpty {
                    return .init(key: "pro.feature.\(feature).body", style: .pro)
                }
                return .init(key: "pro.card.title", style: .pro)
            }
        case "plan.limit":
            return .init(key: details?.limit == nil ? "limit.files.body" : "limit.files.bodyWithLimit", style: .limit)

        // 3.3 Pairing
        case "pair.noActiveSession": return .init(key: "error.pair.noActiveSession", style: .card)
        case "pair.invalidCode": return .init(key: "error.pair.invalidCode", style: .card)
        case "pair.busy": return .init(key: "error.pair.busy", style: .card)
        case "pair.rejected": return .init(key: "error.pair.rejected", style: .card)
        case "pair.expired": return .init(key: "error.pair.expired", style: .card)
        case "pair.deviceLimit": return .init(key: "error.pair.deviceLimit", style: .card)

        // 3.4 Session, power, apps, profiles
        case "session.unavailable":
            if section == "control" || section == "files" {
                return .init(key: "error.session.signIn", style: .tray)
            }
            return .init(key: "status.trayMissing", style: .tray)
        case "power.failed": return .init(key: "error.power.failed", style: .toast)
        case "power.nothingToCancel": return .init(key: "error.power.nothingToCancel", style: .toast)
        case "power.unsupported": return .init(key: "error.power.unsupported", style: .card)
        case "power.lockFailed": return .init(key: "error.power.lockFailed", style: .toast)
        case "apps.notFound": return .init(key: "error.apps.notFound", style: .toast)
        case "apps.launchFailed": return .init(key: "error.apps.launchFailed", style: .toast)
        case "apps.closeNotAllowed": return .init(key: "error.apps.closeNotAllowed", style: .toast)
        case "profiles.notFound": return .init(key: "error.profiles.notFound", style: .toast)
        case "profiles.invalid": return .init(key: "error.profiles.invalid", style: .inline)
        case "profiles.limit": return .init(key: "error.profiles.limit", style: .inline)

        // 3.5 Lighting, files, clipboard, automations
        case "rgb.unavailable": return .init(key: "error.rgb.unavailable", style: .card)
        case "files.notFound": return .init(key: "error.files.notFound", style: .toast)
        case "files.offsetMismatch", "files.uploadNotFound": return .init(key: "error.files.resume", style: .inline)
        case "files.hashMismatch": return .init(key: "error.files.hashMismatch", style: .inline)
        case "files.noSpace": return .init(key: "error.files.noSpace", style: .inline)
        case "files.failed": return .init(key: "error.files.failed", style: .inline)
        case "clipboard.failed": return .init(key: "error.clipboard.failed", style: .toast)
        case "automations.notFound": return .init(key: "error.automations.notFound", style: .toast)
        case "automations.limit": return .init(key: "error.automations.limit", style: .inline)
        case "automations.running": return .init(key: "error.automations.running", style: .toast)

        // 3.6 Screen and input
        case "screen.videoUnavailable", "screen.notStarted": return .init(key: nil, style: .silent)
        case "screen.denied": return .init(key: "error.screen.denied", style: .card)
        case "screen.blocked": return .init(key: "error.screen.blocked", style: .card)
        case "input.blocked": return .init(key: "error.input.blocked", style: .toast)

        // 3.7 Common
        case "rate.limited": return .init(key: "error.rateLimited", style: .toast)
        case "internal": return .init(key: "error.internal", style: .card)

        default: return .init(key: nil, style: .card)
        }
    }

    // MARK: - Reason codes (error-texts.md, 3.8 and 3.9)

    /// What to show as a reason: a String Catalog key, or the PC's own text (fallback only).
    public enum ReasonText: Equatable, Sendable {
        case key(String)
        case pcText(String)
    }

    /// `metrics.snapshot.unavailableCodes` codes with their own text (error-texts.md, 3.8).
    public static let metricReasonCodes: [String] = [
        "sensorDriverMissing", "sensorReadFailed", "sensorMissing", "warmingUp", "osQueryFailed",
        "gpuDriverMissing", "gpuLibraryRejected", "gpuLibraryLoadFailed", "gpuInitFailed", "gpuListFailed",
        "platformUnsupported",
    ]

    /// `monitor.reason.<code>`; an unknown code → `monitor.reason.unknown`.
    public static func metricReasonKey(_ code: String) -> String {
        metricReasonCodes.contains(code) ? "monitor.reason.\(code)" : "monitor.reason.unknown"
    }

    /// Reason for a metric shown as "—": by code; no code → the PC's text from `unavailable`;
    /// neither → `monitor.reason.unknown`.
    public static func metricReason(code: String?, pcText: String?) -> ReasonText {
        if let code, !code.isEmpty {
            return .key(metricReasonKey(code))
        }
        if let text = pcText?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty {
            return .pcText(text)
        }
        return .key("monitor.reason.unknown")
    }

    /// `unavailable` / `unavailableCodes` of `metrics.snapshot`: string values only, anything else is dropped.
    public static func reasonMap(_ value: Any?) -> [String: String] {
        guard let dict = value as? [String: Any] else {
            return [:]
        }
        return dict.compactMapValues { $0 as? String }
    }

    /// `rgb.status.reasonCode` with its own text (error-texts.md, 3.9); unknown or missing → `nil`.
    public static func rgbReasonKey(_ code: String?) -> String? {
        switch code ?? "" {
        case "openrgbNotResponding": return "error.rgb.unavailable"
        case "conflict": return "rgb.busy"
        default: return nil
        }
    }

    /// Text for `rgb.status` with `available: false`: by `reasonCode`, then `state == "conflict"`,
    /// then the PC's `reason`, then `error.rgb.unavailable`.
    public static func rgbUnavailable(_ status: RgbStatus) -> ReasonText {
        if let key = rgbReasonKey(status.reasonCode) {
            return .key(key)
        }
        if status.isConflict {
            return .key("rgb.busy")
        }
        if let text = status.reason?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty {
            return .pcText(text)
        }
        return .key("error.rgb.unavailable")
    }

    /// For an unknown code: the PC's `message` if it is not empty and at most 200 characters,
    /// otherwise `nil` (show `error.generic.body`).
    public static func fallbackMessage(_ message: String?) -> String? {
        guard let text = message?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty, text.count <= 200 else {
            return nil
        }
        return text
    }
}
