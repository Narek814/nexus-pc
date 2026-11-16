import SwiftUI

/// Nexus PC design tokens (ARCHITECTURE.md §7): near-black background, violet and cyan neon accents.
enum Theme {
    static let background = Color(red: 7 / 255, green: 7 / 255, blue: 11 / 255)
    static let surface = Color(red: 22 / 255, green: 22 / 255, blue: 30 / 255)
    static let violet = Color(red: 155 / 255, green: 92 / 255, blue: 1)
    static let cyan = Color(red: 0, green: 220 / 255, blue: 1)
    static let textPrimary = Color(red: 235 / 255, green: 235 / 255, blue: 245 / 255)
    static let textMuted = Color(red: 150 / 255, green: 150 / 255, blue: 170 / 255)

    static let accent = LinearGradient(colors: [violet, cyan], startPoint: .topLeading, endPoint: .bottomTrailing)
    static let cornerRadius: CGFloat = 20
}

/// Glass card with a thin neon edge. Respects Reduce Transparency.
struct GlassCard<Content: View>: View {
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    private let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        content
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background {
                RoundedRectangle(cornerRadius: Theme.cornerRadius, style: .continuous)
                    .fill(reduceTransparency ? AnyShapeStyle(Theme.surface) : AnyShapeStyle(Material.ultraThinMaterial))
            }
            .overlay {
                RoundedRectangle(cornerRadius: Theme.cornerRadius, style: .continuous)
                    .strokeBorder(Theme.accent, lineWidth: 1)
                    .opacity(0.6)
            }
    }
}

/// Primary action button with a soft glow. Glow is static when Reduce Motion is on.
struct NeonButton: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let title: String
    let systemImage: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .font(.headline)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 16)
                .foregroundStyle(Theme.textPrimary)
                .background(Theme.accent, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                .shadow(color: Theme.violet.opacity(0.55), radius: reduceMotion ? 8 : 14)
        }
        .buttonStyle(.plain)
    }
}
