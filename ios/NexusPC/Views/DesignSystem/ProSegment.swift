import NexusKit
import SwiftUI

// MARK: - ProLock

/// Pro feature ids (docs/API.md, «Планы»; windows/Nexus.Core/Entitlements/PlanPolicy.cs).
enum ProFeature {
    static let profiles = "profiles"
    static let rgbSync = "rgb.sync"
    static let automations = "automations"
    static let monitoringExtended = "monitoring.extended"
    static let screenVideo = "screen.video"
}

/// A Pro element inside a section that stays visible in Free. Decided only by `SectionAccess`
/// (step 6) and `hello`: without the `entitlements.v1` marker there is never a lock.
struct ProLock: Equatable, Identifiable {
    let feature: String
    let state: EntitlementState?

    var id: String { feature }

    /// `nil` when the feature is usable (no marker, or the plan has it).
    static func check(_ hello: AgentHello?, feature: String) -> ProLock? {
        // Connection, permission and tray are already settled by the section; only step 6 matters here.
        let access = SectionAccess.resolve(
            isPaired: true, connection: .online, capabilities: [feature], anyOf: [feature],
            userSessionAvailable: true, needsTray: false, hello: hello, requiredFeature: feature)
        if case let .pro(state) = access {
            return ProLock(feature: feature, state: state)
        }
        return nil
    }

    /// Pro ended (`expired` / `revoked`): Automations shows `AutomationsPausedBanner` instead of `ProCard`.
    var isEnded: Bool {
        state == .expired || state == .revoked
    }

    /// `ProCard` text: `pro.feature.<feature>.body`, or the expired / revoked text.
    var text: LocalizedStringResource {
        switch state {
        case .expired: "pro.expired.body"
        case .revoked: "pro.revoked.body"
        default: LocalizedStringResource(stringLiteral: "pro.feature.\(feature).body")
        }
    }
}

// MARK: - ProSegment

/// Segment or row of a Pro option (history 1 h / 24 h, video): a normal segment with `ProBadge`.
/// Choosing it never switches; the caller opens `ProCard` in a sheet. Never shown as selected.
struct ProSegment: View {
    let title: Text
    let action: () -> Void

    init(_ title: Text, action: @escaping () -> Void) {
        self.title = title
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: Theme.Space.xs) {
                title
                    .font(Theme.Typography.callout.weight(.semibold))
                    .foregroundStyle(Theme.Palette.textSecondary)
                    .lineLimit(nil)
                ProBadge()
            }
            .padding(.horizontal, Theme.Space.s)
            .frame(maxWidth: .infinity, minHeight: Theme.Size.hit)
            .background(Theme.Palette.bgSunken, in: RoundedRectangle(cornerRadius: Theme.Radius.control, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

/// Plain segment next to `ProSegment` when the system segmented picker can't host the badge.
struct PlainSegment: View {
    let title: Text
    let isSelected: Bool
    let action: () -> Void

    init(_ title: Text, isSelected: Bool, action: @escaping () -> Void) {
        self.title = title
        self.isSelected = isSelected
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            title
                .font(Theme.Typography.callout.weight(.semibold))
                .foregroundStyle(isSelected ? Theme.Palette.textOnAccent : Theme.Palette.textPrimary)
                .lineLimit(nil)
                .padding(.horizontal, Theme.Space.s)
                .frame(maxWidth: .infinity, minHeight: Theme.Size.hit)
                .background(isSelected ? AnyShapeStyle(Theme.Palette.accentGradient) : AnyShapeStyle(Theme.Palette.bgSunken),
                            in: RoundedRectangle(cornerRadius: Theme.Radius.control, style: .continuous))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(.isButton)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

/// `ProCard` in a sheet, opened from a `ProSegment`.
struct ProCardSheet: View {
    let lock: ProLock

    init(lock: ProLock) {
        self.lock = lock
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                ProCard(feature: lock.text)
                    .padding(Theme.Space.xl)
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }
}
