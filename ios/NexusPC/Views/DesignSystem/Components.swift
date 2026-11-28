import SwiftUI

/// Namespace for design-system components whose names are still taken by the old
/// screen-local versions (`StatusPill`, `PowerTile` in HomeView.swift). When screens move
/// over (E6 screens), the old copies are deleted and `DS.` can be dropped via typealias.
enum DS {}

// MARK: - StatusPill

enum StatusPillState: Equatable, Sendable {
    case ready, connecting, offline, limited
}

extension DS {
    /// Connection status: icon + text, never colour alone. VoiceOver: "Status: PC Ready".
    struct StatusPill: View {
        let state: StatusPillState
        @ScaledMetric(relativeTo: .caption) private var iconSize: CGFloat = 12

        init(state: StatusPillState) {
            self.state = state
        }

        var body: some View {
            HStack(spacing: Theme.Space.xs) {
                icon.frame(width: iconSize, height: iconSize)
                Text(title)
                    .font(Theme.Typography.caption.weight(.semibold))
                    .foregroundStyle(Theme.Palette.textPrimary)
            }
            .padding(.horizontal, Theme.Space.s)
            .padding(.vertical, Theme.Space.xs)
            .background(Theme.Palette.bgElevated, in: Capsule())
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(Text("status.a11y \(String(localized: title))"))
        }

        private var title: LocalizedStringResource {
            switch state {
            case .ready: "status.ready"
            case .connecting: "status.connecting"
            case .offline: "status.offline"
            case .limited: "status.limited"
            }
        }

        @ViewBuilder private var icon: some View {
            switch state {
            case .ready:
                Image(systemName: "checkmark.circle.fill").resizable().scaledToFit()
                    .foregroundStyle(Theme.Palette.stateSuccess)
            case .connecting:
                ProgressView().controlSize(.mini).tint(Theme.Palette.textSecondary)
            case .offline:
                Image(systemName: "wifi.slash").resizable().scaledToFit()
                    .foregroundStyle(Theme.Palette.stateOffline)
            case .limited:
                Image(systemName: "exclamationmark.triangle.fill").resizable().scaledToFit()
                    .foregroundStyle(Theme.Palette.stateWarning)
            }
        }
    }
}

// MARK: - ProBadge

/// "Pro" capsule. VoiceOver: "Requires Pro".
struct ProBadge: View {
    var body: some View {
        Text("pro.badge")
            .font(Theme.Typography.caption.weight(.bold))
            .foregroundStyle(Theme.Palette.textOnAccent)
            .padding(.horizontal, Theme.Space.s)
            .padding(.vertical, Theme.Space.xs / 2)
            .background(Theme.Palette.statePro, in: Capsule())
            .accessibilityLabel(Text("pro.badge.a11y"))
    }
}

// MARK: - SectionHeader

/// Section header (`type.section`, uppercase), optional trailing "See All" button.
struct SectionHeader: View {
    let title: LocalizedStringResource
    var onSeeAll: (() -> Void)?

    init(_ title: LocalizedStringResource, onSeeAll: (() -> Void)? = nil) {
        self.title = title
        self.onSeeAll = onSeeAll
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title)
                .font(Theme.Typography.section)
                .textCase(.uppercase)
                .tracking(Theme.Typography.sectionTracking)
                .foregroundStyle(Theme.Palette.textSecondary)
                .lineLimit(nil)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: Theme.Space.s)
            if let onSeeAll {
                Button("common.seeAll", action: onSeeAll)
                    .font(Theme.Typography.callout.weight(.semibold))
                    .foregroundStyle(Theme.Palette.accentPrimary)
                    .frame(minHeight: Theme.Size.hit)
            }
        }
        .padding(.top, Theme.Space.xs)
    }
}

// MARK: - Toast

enum ToastKind: Equatable, Sendable {
    case info, success, error
}

/// Transient message at the top. Announced to VoiceOver; 3 s, or 6 s with VoiceOver on.
/// Place inside a container animated with `Theme.Motion.standard` and use `Toast.transition`.
struct Toast: View {
    let message: String
    var kind: ToastKind = .info
    let onDismiss: () -> Void
    @Environment(\.accessibilityVoiceOverEnabled) private var voiceOver

    init(_ message: String, kind: ToastKind = .info, onDismiss: @escaping () -> Void) {
        self.message = message
        self.kind = kind
        self.onDismiss = onDismiss
    }

    init(_ message: LocalizedStringResource, kind: ToastKind = .info, onDismiss: @escaping () -> Void) {
        self.init(String(localized: message), kind: kind, onDismiss: onDismiss)
    }

    static func transition(reduceMotion: Bool) -> AnyTransition {
        Theme.Motion.toastTransition(reduceMotion: reduceMotion)
    }

    var body: some View {
        HStack(spacing: Theme.Space.s) {
            if let icon {
                Image(systemName: icon.name)
                    .foregroundStyle(icon.color)
                    .accessibilityHidden(true)
            }
            Text(message)
                .font(Theme.Typography.callout.weight(.semibold))
                .foregroundStyle(Theme.Palette.textPrimary)
                .lineLimit(nil)
                .multilineTextAlignment(.leading)
        }
        .padding(.horizontal, Theme.Space.l)
        .padding(.vertical, Theme.Space.s + Theme.Space.xs / 2)
        .background(Theme.Palette.bgElevated, in: Capsule())
        .overlay(Capsule().strokeBorder(Theme.Palette.accentGradient, lineWidth: Theme.Size.strokeCard))
        .padding(.top, Theme.Space.s)
        .accessibilityElement(children: .combine)
        .task(id: message) {
            AccessibilityNotification.Announcement(message).post()
            try? await Task.sleep(for: voiceOver ? Theme.Motion.toastDurationVoiceOver : Theme.Motion.toastDuration)
            if !Task.isCancelled { onDismiss() }
        }
    }

    private var icon: (name: String, color: SwiftUI.Color)? {
        switch kind {
        case .info: nil
        case .success: ("checkmark.circle.fill", Theme.Palette.stateSuccess)
        case .error: ("exclamationmark.triangle.fill", Theme.Palette.stateDanger)
        }
    }
}

// MARK: - MetricGauge

/// Ring gauge 0–100. No value → "—" without a ring, VoiceOver "CPU: Unavailable".
struct MetricGauge: View {
    let title: String
    let value: Double?
    let text: String
    var tint: SwiftUI.Color = Theme.Palette.accentSecondary
    var isLoading = false
    private var a11y = DesignAccessibility()
    @ScaledMetric(relativeTo: .title3) private var diameter: CGFloat = 66
    @ScaledMetric(relativeTo: .title3) private var lineWidth: CGFloat = 7

    init(title: String, value: Double?, text: String, tint: SwiftUI.Color = Theme.Palette.accentSecondary, isLoading: Bool = false) {
        self.title = title
        self.value = value
        self.text = text
        self.tint = tint
        self.isLoading = isLoading
    }

    var body: some View {
        VStack(spacing: Theme.Space.xs + Theme.Space.xs / 2) {
            ZStack {
                Circle().stroke(Theme.Palette.bgElevated, lineWidth: lineWidth)
                if isLoading {
                    ProgressView().tint(Theme.Palette.textSecondary)
                } else {
                    if let value {
                        Circle()
                            .trim(from: 0, to: min(max(value / 100, 0), 1))
                            .stroke(tint, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                            .rotationEffect(.degrees(-90))
                            .shadow(color: tint.opacity(0.7), radius: a11y.reduceMotion ? 2 : 4)
                            .animation(a11y.reduceMotion ? nil : .easeOut(duration: 0.4), value: value)
                    }
                    Text(value == nil ? "—" : text)
                        .font(Theme.Typography.metric)
                        .monospacedDigit()
                        .minimumScaleFactor(Theme.Typography.metricMinimumScale)
                        .lineLimit(1)
                        .padding(.horizontal, lineWidth)
                        .foregroundStyle(Theme.Palette.textPrimary)
                }
            }
            .frame(width: diameter, height: diameter)
            Text(title)
                .font(Theme.Typography.caption.weight(.semibold))
                .foregroundStyle(Theme.Palette.textSecondary)
                .lineLimit(nil)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: "\(title): \(accessibilityText)"))
    }

    private var accessibilityText: String {
        if isLoading { return String(localized: "common.loading") }
        if value == nil { return String(localized: "common.unavailable") }
        return text
    }
}

// MARK: - PowerTile

extension DS {
    /// Power action tile. Disabled when the PC is offline (use `.disabled`); `isLoading` blocks taps.
    /// Confirmation for shutdown/restart stays in the screen (`confirmationDialog`).
    struct PowerTile: View {
        let title: LocalizedStringResource
        let systemImage: String
        var tint: SwiftUI.Color = Theme.Palette.accentPrimary
        var isLoading = false
        let action: () -> Void
        @Environment(\.isEnabled) private var isEnabled

        init(title: LocalizedStringResource, systemImage: String, tint: SwiftUI.Color = Theme.Palette.accentPrimary, isLoading: Bool = false, action: @escaping () -> Void) {
            self.title = title
            self.systemImage = systemImage
            self.tint = tint
            self.isLoading = isLoading
            self.action = action
        }

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: Theme.Radius.control, style: .continuous)
            Button(action: action) {
                VStack(spacing: Theme.Space.s) {
                    if isLoading {
                        ProgressView().tint(tint)
                    } else {
                        Image(systemName: systemImage).font(Theme.Typography.title).accessibilityHidden(true)
                    }
                    Text(title)
                        .font(Theme.Typography.callout.weight(.semibold))
                        .lineLimit(nil)
                        .multilineTextAlignment(.center)
                }
                .foregroundStyle(isEnabled ? tint : Theme.Palette.textSecondary)
                .frame(maxWidth: .infinity, minHeight: Theme.Size.hit)
                .padding(.vertical, Theme.Space.l)
                .padding(.horizontal, Theme.Space.s)
                .background(Theme.Palette.bgElevated, in: shape)
                .overlay(shape.strokeBorder(isEnabled ? tint : Theme.Palette.bgElevated, lineWidth: Theme.Size.strokeCard).opacity(Theme.Palette.borderCardOpacity))
                .contentShape(shape)
            }
            .buttonStyle(PressableButtonStyle())
            .allowsHitTesting(!isLoading)
            .accessibilityLabel(Text(title))
            .accessibilityValue(isLoading ? Text("common.inProgress") : Text(verbatim: ""))
        }
    }

    /// Row of 2–4 tiles; becomes one column at accessibility text sizes.
    struct TileRow<Content: View>: View {
        @Environment(\.dynamicTypeSize) private var dynamicTypeSize
        private let content: Content

        init(@ViewBuilder content: () -> Content) {
            self.content = content()
        }

        var body: some View {
            let layout = dynamicTypeSize.isAccessibilitySize
                ? AnyLayout(VStackLayout(spacing: Theme.Space.m))
                : AnyLayout(HStackLayout(spacing: Theme.Space.m))
            layout { content }
        }
    }
}
