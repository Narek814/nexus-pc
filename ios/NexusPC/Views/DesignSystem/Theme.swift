import SwiftUI

/// Nexus PC design tokens (docs/design/foundations.md). Views use only semantic tokens:
/// no colour, font-size or spacing literals outside DesignSystem.
enum Theme {
    // MARK: Colour tokens (foundations §2). Values live in Assets.xcassets/DesignSystem (Any + Dark).

    enum Palette {
        /// bg.base — screen background.
        static let bgBase = SwiftUI.Color("BgBase")
        /// bg.elevated — cards under Reduce Transparency, pills.
        static let bgElevated = SwiftUI.Color("BgElevated")
        /// bg.sunken — inputs, touchpad area.
        static let bgSunken = SwiftUI.Color("BgSunken")
        /// text.primary
        static let textPrimary = SwiftUI.Color("TextPrimary")
        /// text.secondary
        static let textSecondary = SwiftUI.Color("TextSecondary")
        /// text.onAccent — text and icons on the accent gradient (contrast ≥ 5:1).
        static let textOnAccent = SwiftUI.Color("TextOnAccent")
        /// accent.primary — links, selection.
        static let accentPrimary = SwiftUI.Color("AccentPrimary")
        /// accent.secondary
        static let accentSecondary = SwiftUI.Color("AccentSecondary")
        /// border.subtle — separators.
        static let borderSubtle = SwiftUI.Color("BorderSubtle")
        /// state.success
        static let stateSuccess = SwiftUI.Color("StateSuccess")
        /// state.warning
        static let stateWarning = SwiftUI.Color("StateWarning")
        /// state.danger
        static let stateDanger = SwiftUI.Color("StateDanger")
        /// state.offline (= text.secondary)
        static let stateOffline = textSecondary
        /// glow.accent — accent.primary at 55 %.
        static let glowAccent = accentPrimary.opacity(0.55)

        /// accent.gradient — primary → secondary, top-leading to bottom-trailing.
        static let accentGradient = LinearGradient(
            colors: [accentPrimary, accentSecondary],
            startPoint: .topLeading,
            endPoint: .bottomTrailing
        )
        /// state.pro
        static let statePro = accentGradient
        /// border.card opacity applied over accent.gradient.
        static let borderCardOpacity: Double = 0.6
    }

    // MARK: Typography (foundations §3). Text styles only — scale with Dynamic Type up to AX5.

    enum Typography {
        static let display = SwiftUI.Font.system(.largeTitle, design: .rounded, weight: .heavy)
        static let title = SwiftUI.Font.title2.weight(.semibold)
        static let headline = SwiftUI.Font.headline
        static let body = SwiftUI.Font.body
        static let callout = SwiftUI.Font.subheadline
        static let caption = SwiftUI.Font.caption
        /// Used with `.textCase(.uppercase)` and `sectionTracking`.
        static let section = SwiftUI.Font.footnote.weight(.semibold)
        static let sectionTracking: CGFloat = 0.5
        /// Combine with `.monospacedDigit()`.
        static let metric = SwiftUI.Font.title3.weight(.semibold)
        static let mono = SwiftUI.Font.system(.body, design: .monospaced)
        /// Lowest scale factor allowed, and only for metric digits.
        static let metricMinimumScale: CGFloat = 0.8
    }

    // MARK: Spacing, radii, sizes (foundations §4)

    enum Space {
        static let xs: CGFloat = 4
        static let s: CGFloat = 8
        static let m: CGFloat = 12
        static let l: CGFloat = 16
        static let xl: CGFloat = 20
        static let xxl: CGFloat = 32
    }

    enum Radius {
        static let card: CGFloat = 20
        static let control: CGFloat = 16
    }

    enum Size {
        /// Minimum hit area (HIG).
        static let hit: CGFloat = 44
        static let strokeCard: CGFloat = 1
        static let strokeCardIncreased: CGFloat = 2
        static let glowRadius: CGFloat = 14
        static let glowRadiusReduced: CGFloat = 8
        static let disabledOpacity: Double = 0.4
        static let pressedScale: CGFloat = 0.97
    }

    // MARK: Motion (foundations §5)

    enum Motion {
        static func standard(reduceMotion: Bool) -> Animation? {
            reduceMotion ? .easeInOut(duration: 0.15) : .snappy(duration: 0.25)
        }

        static func toastTransition(reduceMotion: Bool) -> AnyTransition {
            reduceMotion ? .opacity : .move(edge: .top).combined(with: .opacity)
        }

        static func stepTransition(reduceMotion: Bool) -> AnyTransition {
            reduceMotion ? .opacity : .asymmetric(
                insertion: .move(edge: .trailing).combined(with: .opacity),
                removal: .move(edge: .leading).combined(with: .opacity)
            )
        }

        static let toastDuration: Duration = .seconds(3)
        static let toastDurationVoiceOver: Duration = .seconds(6)
        static let loadingSlowAfter: Duration = .seconds(10)
    }

    // MARK: Deprecated aliases — kept until screens move to semantic tokens (E6 screens).

    @available(*, deprecated, renamed: "Palette.bgBase")
    static var background: SwiftUI.Color { Palette.bgBase }
    @available(*, deprecated, renamed: "Palette.bgElevated")
    static var surface: SwiftUI.Color { Palette.bgElevated }
    @available(*, deprecated, renamed: "Palette.accentPrimary")
    static var violet: SwiftUI.Color { Palette.accentPrimary }
    @available(*, deprecated, renamed: "Palette.accentSecondary")
    static var cyan: SwiftUI.Color { Palette.accentSecondary }
    @available(*, deprecated, renamed: "Palette.textPrimary")
    static var textPrimary: SwiftUI.Color { Palette.textPrimary }
    @available(*, deprecated, renamed: "Palette.textSecondary")
    static var textMuted: SwiftUI.Color { Palette.textSecondary }
    @available(*, deprecated, renamed: "Palette.accentGradient")
    static var accent: LinearGradient { Palette.accentGradient }
    @available(*, deprecated, renamed: "Radius.card")
    static var cornerRadius: CGFloat { Radius.card }
}

// MARK: - Accessibility settings with preview overrides

/// Overrides for system accessibility settings. Only used by `#Preview`s: the system
/// values (`accessibilityReduceMotion` etc.) cannot be set from the environment.
struct DesignAccessibilityOverride: Equatable, Sendable {
    var reduceMotion: Bool?
    var reduceTransparency: Bool?
    var increasedContrast: Bool?
}

private struct DesignAccessibilityOverrideKey: EnvironmentKey {
    static let defaultValue = DesignAccessibilityOverride()
}

extension EnvironmentValues {
    var designAccessibilityOverride: DesignAccessibilityOverride {
        get { self[DesignAccessibilityOverrideKey.self] }
        set { self[DesignAccessibilityOverrideKey.self] = newValue }
    }
}

/// System accessibility settings as the design system sees them.
struct DesignAccessibility: DynamicProperty {
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @Environment(\.accessibilityReduceTransparency) private var systemReduceTransparency
    @Environment(\.colorSchemeContrast) private var systemContrast
    @Environment(\.designAccessibilityOverride) private var override

    var reduceMotion: Bool { override.reduceMotion ?? systemReduceMotion }
    var reduceTransparency: Bool { override.reduceTransparency ?? systemReduceTransparency }
    var increasedContrast: Bool { override.increasedContrast ?? (systemContrast == .increased) }

    var cardStroke: CGFloat { increasedContrast ? Theme.Size.strokeCardIncreased : Theme.Size.strokeCard }
    var cardStrokeOpacity: Double { increasedContrast ? 1 : Theme.Palette.borderCardOpacity }
}

extension View {
    /// Preview helper: simulate Reduce Motion / Reduce Transparency / Increased Contrast.
    func designAccessibility(reduceMotion: Bool? = nil, reduceTransparency: Bool? = nil, increasedContrast: Bool? = nil) -> some View {
        environment(\.designAccessibilityOverride, DesignAccessibilityOverride(
            reduceMotion: reduceMotion,
            reduceTransparency: reduceTransparency,
            increasedContrast: increasedContrast
        ))
    }
}

// MARK: - Press style (motion.press)

/// Pressed = scale 0.97; with Reduce Motion only the opacity changes. Disabled = 40 %.
struct PressableButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        PressableBody(configuration: configuration)
    }

    private struct PressableBody: View {
        let configuration: ButtonStyleConfiguration
        @Environment(\.isEnabled) private var isEnabled
        private var a11y = DesignAccessibility()

        var body: some View {
            configuration.label
                .scaleEffect(configuration.isPressed && !a11y.reduceMotion ? Theme.Size.pressedScale : 1)
                .opacity(isEnabled ? (configuration.isPressed && a11y.reduceMotion ? 0.7 : 1) : Theme.Size.disabledOpacity)
                .animation(Theme.Motion.standard(reduceMotion: a11y.reduceMotion), value: configuration.isPressed)
        }
    }
}

// MARK: - GlassCard

/// Glass card with a thin neon edge. Reduce Transparency → bg.elevated; Increased Contrast → 2 pt, 100 %.
struct GlassCard<Content: View>: View {
    private var a11y = DesignAccessibility()
    @Environment(\.isEnabled) private var isEnabled
    @ScaledMetric(relativeTo: .body) private var padding = Theme.Space.xl
    private let isHighlighted: Bool
    private let content: Content

    init(isHighlighted: Bool = false, @ViewBuilder content: () -> Content) {
        self.isHighlighted = isHighlighted
        self.content = content()
    }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Theme.Radius.card, style: .continuous)
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background {
                shape.fill(a11y.reduceTransparency
                    ? AnyShapeStyle(Theme.Palette.bgElevated)
                    : AnyShapeStyle(Material.ultraThinMaterial))
            }
            .overlay {
                shape
                    .strokeBorder(Theme.Palette.accentGradient,
                                  lineWidth: isHighlighted ? Theme.Size.strokeCardIncreased : a11y.cardStroke)
                    .opacity(isHighlighted ? 1 : a11y.cardStrokeOpacity)
            }
            .opacity(isEnabled ? 1 : Theme.Size.disabledOpacity)
    }
}

// MARK: - NeonButton

/// Primary action button: accent gradient, text.onAccent, static glow (8 with Reduce Motion).
/// `isLoading` shows a spinner instead of the icon, blocks taps and reads "…, in progress".
struct NeonButton: View {
    private var a11y = DesignAccessibility()
    @Environment(\.isEnabled) private var isEnabled
    let title: LocalizedStringResource
    let systemImage: String
    var role: ButtonRole?
    var isLoading = false
    let action: () -> Void

    init(title: LocalizedStringResource, systemImage: String, role: ButtonRole? = nil, isLoading: Bool = false, action: @escaping () -> Void) {
        self.title = title
        self.systemImage = systemImage
        self.role = role
        self.isLoading = isLoading
        self.action = action
    }

    private var isDestructive: Bool { role == .destructive }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Theme.Radius.control, style: .continuous)
        Button(role: role, action: action) {
            HStack(spacing: Theme.Space.s) {
                if isLoading {
                    ProgressView().tint(Theme.Palette.textOnAccent)
                } else {
                    Image(systemName: systemImage).accessibilityHidden(true)
                }
                Text(title)
            }
            .font(Theme.Typography.headline)
            .multilineTextAlignment(.center)
            .frame(maxWidth: .infinity, minHeight: Theme.Size.hit)
            .padding(.vertical, Theme.Space.s)
            .padding(.horizontal, Theme.Space.l)
            .foregroundStyle(Theme.Palette.textOnAccent)
            .background {
                if isDestructive {
                    shape.fill(Theme.Palette.stateDanger)
                } else {
                    shape.fill(Theme.Palette.accentGradient)
                }
            }
            .shadow(color: glowColor, radius: a11y.reduceMotion ? Theme.Size.glowRadiusReduced : Theme.Size.glowRadius)
            .contentShape(shape)
        }
        .buttonStyle(PressableButtonStyle())
        .allowsHitTesting(!isLoading)
        .accessibilityLabel(Text(title))
        .accessibilityValue(isLoading ? Text("common.inProgress") : Text(verbatim: ""))
    }

    private var glowColor: SwiftUI.Color {
        guard isEnabled, !isLoading else { return .clear }
        return isDestructive ? Theme.Palette.stateDanger.opacity(0.55) : Theme.Palette.glowAccent
    }
}

// MARK: - SecondaryButton

/// Secondary action: card border, accent.primary text. Same states as NeonButton.
struct SecondaryButton: View {
    private var a11y = DesignAccessibility()
    let title: LocalizedStringResource
    var systemImage: String?
    var isLoading = false
    let action: () -> Void

    init(title: LocalizedStringResource, systemImage: String? = nil, isLoading: Bool = false, action: @escaping () -> Void) {
        self.title = title
        self.systemImage = systemImage
        self.isLoading = isLoading
        self.action = action
    }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Theme.Radius.control, style: .continuous)
        Button(action: action) {
            HStack(spacing: Theme.Space.s) {
                if isLoading {
                    ProgressView().tint(Theme.Palette.accentPrimary)
                } else if let systemImage {
                    Image(systemName: systemImage).accessibilityHidden(true)
                }
                Text(title)
            }
            .font(Theme.Typography.headline)
            .multilineTextAlignment(.center)
            .foregroundStyle(Theme.Palette.accentPrimary)
            .frame(maxWidth: .infinity, minHeight: Theme.Size.hit)
            .padding(.vertical, Theme.Space.xs)
            .padding(.horizontal, Theme.Space.l)
            .overlay {
                shape.strokeBorder(Theme.Palette.accentGradient, lineWidth: a11y.cardStroke)
                    .opacity(a11y.cardStrokeOpacity)
            }
            .contentShape(shape)
        }
        .buttonStyle(PressableButtonStyle())
        .allowsHitTesting(!isLoading)
        .accessibilityLabel(Text(title))
        .accessibilityValue(isLoading ? Text("common.inProgress") : Text(verbatim: ""))
    }
}
