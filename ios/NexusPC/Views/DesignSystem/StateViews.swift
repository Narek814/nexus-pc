import SwiftUI

// State components (foundations §6.2). Priority when several apply:
// offline → permission → Pro → error → loading → empty → content.

// MARK: - Shared card header

private struct CardHeader: View {
    let systemImage: String
    let tint: SwiftUI.Color
    let title: Text

    var body: some View {
        Label {
            title
                .font(Theme.Typography.headline)
                .foregroundStyle(Theme.Palette.textPrimary)
                .lineLimit(nil)
                .fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: systemImage)
                .foregroundStyle(tint)
                .accessibilityHidden(true)
        }
        .accessibilityAddTraits(.isHeader)
    }
}

private struct CardBody: View {
    let text: Text

    var body: some View {
        text
            .font(Theme.Typography.callout)
            .foregroundStyle(Theme.Palette.textSecondary)
            .lineLimit(nil)
            .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: - NavRow

enum NavRowBadge: Equatable, Sendable {
    case none, pro, permissionNeeded
}

/// Row used as a `NavigationLink` label in the Control / PC / More roots.
/// Use with `.buttonStyle(PressableButtonStyle())` on the link. One VoiceOver element.
struct NavRow: View {
    let title: LocalizedStringResource
    let systemImage: String
    var badge: NavRowBadge = .none
    @ScaledMetric(relativeTo: .body) private var iconWidth: CGFloat = 28

    init(title: LocalizedStringResource, systemImage: String, badge: NavRowBadge = .none) {
        self.title = title
        self.systemImage = systemImage
        self.badge = badge
    }

    var body: some View {
        GlassCard {
            HStack(spacing: Theme.Space.m) {
                Image(systemName: systemImage)
                    .font(Theme.Typography.headline)
                    .foregroundStyle(Theme.Palette.accentSecondary)
                    .frame(width: iconWidth)
                    .accessibilityHidden(true)
                Text(title)
                    .font(Theme.Typography.body)
                    .foregroundStyle(Theme.Palette.textPrimary)
                    .lineLimit(nil)
                Spacer(minLength: Theme.Space.s)
                switch badge {
                case .none:
                    EmptyView()
                case .pro:
                    ProBadge()
                case .permissionNeeded:
                    Image(systemName: "lock.fill")
                        .foregroundStyle(Theme.Palette.stateWarning)
                        .accessibilityLabel(Text("permission.title"))
                }
                Image(systemName: "chevron.right")
                    .font(Theme.Typography.callout.weight(.semibold))
                    .foregroundStyle(Theme.Palette.textSecondary)
                    .accessibilityHidden(true)
            }
            .frame(minHeight: Theme.Size.hit)
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}

// MARK: - EmptyState

/// No data or no PC. Large decorative icon, title, explanation, 0–1 primary action.
struct EmptyState: View {
    let systemImage: String
    let title: LocalizedStringResource
    let message: LocalizedStringResource
    var actionTitle: LocalizedStringResource?
    var actionImage: String = "plus"
    var action: (() -> Void)?

    init(systemImage: String, title: LocalizedStringResource, message: LocalizedStringResource,
         actionTitle: LocalizedStringResource? = nil, actionImage: String = "plus", action: (() -> Void)? = nil) {
        self.systemImage = systemImage
        self.title = title
        self.message = message
        self.actionTitle = actionTitle
        self.actionImage = actionImage
        self.action = action
    }

    /// "No PC Connected" → "Connect PC".
    static func noPC(action: @escaping () -> Void) -> EmptyState {
        EmptyState(systemImage: "desktopcomputer", title: "empty.noPC.title", message: "empty.noPC.body",
                   actionTitle: "empty.noPC.action", actionImage: "qrcode.viewfinder", action: action)
    }

    var body: some View {
        VStack(spacing: Theme.Space.l) {
            Image(systemName: systemImage)
                .font(Theme.Typography.display)
                .foregroundStyle(Theme.Palette.accentGradient)
                .accessibilityHidden(true)
            Text(title)
                .font(Theme.Typography.title)
                .foregroundStyle(Theme.Palette.textPrimary)
                .multilineTextAlignment(.center)
                .lineLimit(nil)
                .accessibilityAddTraits(.isHeader)
            Text(message)
                .font(Theme.Typography.body)
                .foregroundStyle(Theme.Palette.textSecondary)
                .multilineTextAlignment(.center)
                .lineLimit(nil)
                .fixedSize(horizontal: false, vertical: true)
            if let actionTitle, let action {
                NeonButton(title: actionTitle, systemImage: actionImage, action: action)
                    .padding(.top, Theme.Space.s)
            }
        }
        .padding(Theme.Space.xl)
        .frame(maxWidth: .infinity)
    }
}

// MARK: - LoadingState

/// First load of a section. After 10 s: "taking longer" text and, if given, "Try Again".
struct LoadingState: View {
    var onRetry: (() -> Void)?
    @State private var isSlow = false

    init(onRetry: (() -> Void)? = nil) {
        self.onRetry = onRetry
    }

    /// Preview / test hook: start in the slow state.
    init(startSlow: Bool, onRetry: (() -> Void)? = nil) {
        self.onRetry = onRetry
        _isSlow = State(initialValue: startSlow)
    }

    var body: some View {
        VStack(spacing: Theme.Space.m) {
            ProgressView().tint(Theme.Palette.textSecondary)
            Text(isSlow ? LocalizedStringKey("common.loadingSlow") : LocalizedStringKey("common.loading"))
                .font(Theme.Typography.callout)
                .foregroundStyle(Theme.Palette.textSecondary)
                .multilineTextAlignment(.center)
                .lineLimit(nil)
            if isSlow, let onRetry {
                SecondaryButton(title: "common.retry", systemImage: "arrow.clockwise", action: onRetry)
            }
        }
        .padding(Theme.Space.xl)
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .contain)
        .task {
            guard !isSlow else { return }
            try? await Task.sleep(for: Theme.Motion.loadingSlowAfter)
            if !Task.isCancelled { isSlow = true }
        }
    }
}

// MARK: - ErrorCard

/// Request error. `message` is chosen by the caller from the stable error `code`
/// (PC text only as fallback). `isRetrying` puts "Try Again" into the loading state.
struct ErrorCard: View {
    var title: LocalizedStringResource = "common.error.title"
    let message: String
    var isRetrying = false
    var onRetry: (() -> Void)?

    init(title: LocalizedStringResource = "common.error.title", message: String, isRetrying: Bool = false, onRetry: (() -> Void)? = nil) {
        self.title = title
        self.message = message
        self.isRetrying = isRetrying
        self.onRetry = onRetry
    }

    var body: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: Theme.Space.m) {
                CardHeader(systemImage: "exclamationmark.triangle.fill", tint: Theme.Palette.stateDanger, title: Text(title))
                CardBody(text: Text(message))
                if let onRetry {
                    SecondaryButton(title: "common.retry", systemImage: "arrow.clockwise", isLoading: isRetrying, action: onRetry)
                }
            }
        }
    }
}

// MARK: - OfflineCard

/// PC offline / no connection. Replaces section content; on Home lives inside the status card.
struct OfflineCard: View {
    var isRetrying = false
    let onRetry: () -> Void
    var isWaking = false
    /// Non-nil only when Wake-on-LAN is available.
    var onWake: (() -> Void)?

    init(isRetrying: Bool = false, onRetry: @escaping () -> Void, isWaking: Bool = false, onWake: (() -> Void)? = nil) {
        self.isRetrying = isRetrying
        self.onRetry = onRetry
        self.isWaking = isWaking
        self.onWake = onWake
    }

    var body: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: Theme.Space.m) {
                CardHeader(systemImage: "wifi.slash", tint: Theme.Palette.stateOffline, title: Text("status.offline"))
                CardBody(text: Text("offline.body"))
                if let onWake {
                    NeonButton(title: "offline.wake", systemImage: "power.circle", isLoading: isWaking, action: onWake)
                }
                SecondaryButton(title: "common.retry", systemImage: "arrow.clockwise", isLoading: isRetrying, action: onRetry)
            }
        }
    }
}

// MARK: - PermissionCard

/// Permission names — must match the tray window "Devices and Permissions" (E5-4).
enum PermissionName: String, CaseIterable, Sendable {
    case input, screen, files, clipboard, automation, power, apps, monitoring, rgb

    var title: LocalizedStringResource {
        switch self {
        case .input: "permission.name.input"
        case .screen: "permission.name.screen"
        case .files: "permission.name.files"
        case .clipboard: "permission.name.clipboard"
        case .automation: "permission.name.automation"
        case .power: "permission.name.power"
        case .apps: "permission.name.apps"
        case .monitoring: "permission.name.monitoring"
        case .rgb: "permission.name.rgb"
        }
    }
}

/// "Unavailable — turn it on on the PC". No GUIDs, no commands.
struct PermissionCard: View {
    let permission: PermissionName
    var isChecking = false
    let onCheckAgain: () -> Void

    init(permission: PermissionName, isChecking: Bool = false, onCheckAgain: @escaping () -> Void) {
        self.permission = permission
        self.isChecking = isChecking
        self.onCheckAgain = onCheckAgain
    }

    var body: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: Theme.Space.m) {
                CardHeader(systemImage: "lock.fill", tint: Theme.Palette.stateWarning, title: Text("permission.title"))
                CardBody(text: Text(path))
                SecondaryButton(title: "permission.checkAgain", systemImage: "arrow.clockwise", isLoading: isChecking, action: onCheckAgain)
            }
        }
    }

    private var path: String {
        String(format: String(localized: "permission.path"), String(localized: permission.title))
    }
}

// MARK: - TrayMissingCard

/// The PC service runs but the tray app is not started.
struct TrayMissingCard: View {
    var body: some View {
        Label {
            Text("status.trayMissing")
                .font(Theme.Typography.callout)
                .foregroundStyle(Theme.Palette.textPrimary)
                .lineLimit(nil)
                .fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: "exclamationmark.triangle")
                .foregroundStyle(Theme.Palette.stateWarning)
                .accessibilityHidden(true)
        }
        .padding(Theme.Space.m)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.Palette.bgElevated, in: RoundedRectangle(cornerRadius: Theme.Radius.control, style: .continuous))
        .accessibilityElement(children: .combine)
    }
}

// MARK: - ProCard

/// "Needs Pro". No prices. `isStoreReachable == false` disables the button with an explanation.
/// Also shown when the PC answers `plan.required`. Without `onLearnMore` there is no button
/// (owner's decision: hidden until the Pro screen exists).
struct ProCard: View {
    let feature: LocalizedStringResource
    var isStoreReachable = true
    let onLearnMore: (() -> Void)?

    init(feature: LocalizedStringResource, isStoreReachable: Bool = true, onLearnMore: (() -> Void)? = nil) {
        self.feature = feature
        self.isStoreReachable = isStoreReachable
        self.onLearnMore = onLearnMore
    }

    var body: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: Theme.Space.m) {
                HStack(spacing: Theme.Space.s) {
                    ProBadge()
                    Text("pro.card.title")
                        .font(Theme.Typography.headline)
                        .foregroundStyle(Theme.Palette.textPrimary)
                        .lineLimit(nil)
                        .accessibilityAddTraits(.isHeader)
                }
                CardBody(text: Text(feature))
                if let onLearnMore {
                    NeonButton(title: "pro.card.more", systemImage: "sparkles", action: onLearnMore)
                        .disabled(!isStoreReachable)
                    if !isStoreReachable {
                        CardBody(text: Text("pro.card.offline"))
                    }
                }
            }
        }
    }
}

// MARK: - LimitNotice

/// `plan.limit`. No numbers unless `detail` comes from the server. Without `onLearnMore` there is
/// no button (same rule as `ProCard`).
struct LimitNotice: View {
    var detail: String?
    let onLearnMore: (() -> Void)?

    init(detail: String? = nil, onLearnMore: (() -> Void)? = nil) {
        self.detail = detail
        self.onLearnMore = onLearnMore
    }

    var body: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: Theme.Space.m) {
                CardHeader(systemImage: "exclamationmark.circle.fill", tint: Theme.Palette.stateWarning, title: Text("limit.title"))
                if let detail {
                    CardBody(text: Text(detail))
                }
                if let onLearnMore {
                    SecondaryButton(title: "pro.card.more", systemImage: "sparkles", action: onLearnMore)
                }
            }
        }
    }
}
