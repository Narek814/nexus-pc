#if DEBUG
import SwiftUI

// Preview check for every component state (E6-DS acceptance): default, AX5, Reduce Motion,
// Reduce Transparency / Increased Contrast. Debug only.

private struct PreviewCanvas<Content: View>: View {
    private let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Theme.Space.l) { content }
                .padding(Theme.Space.xl)
        }
        .background(Theme.Palette.bgBase)
        .preferredColorScheme(.dark)
    }
}

private struct ButtonsGallery: View {
    var body: some View {
        PreviewCanvas {
            SectionHeader("pro.card.title", onSeeAll: {})
            NeonButton(title: "empty.noPC.action", systemImage: "qrcode.viewfinder") {}
            NeonButton(title: "offline.wake", systemImage: "power.circle", isLoading: true) {}
            NeonButton(title: "offline.wake", systemImage: "power.circle") {}.disabled(true)
            NeonButton(title: "common.cancel", systemImage: "power", role: .destructive) {}
            SecondaryButton(title: "common.retry", systemImage: "arrow.clockwise") {}
            SecondaryButton(title: "common.retry", systemImage: "arrow.clockwise", isLoading: true) {}
            SecondaryButton(title: "common.retry") {}.disabled(true)
            DS.TileRow {
                DS.PowerTile(title: "permission.name.power", systemImage: "power") {}
                DS.PowerTile(title: "permission.name.power", systemImage: "moon.fill", isLoading: true) {}
                DS.PowerTile(title: "permission.name.power", systemImage: "lock.fill") {}.disabled(true)
            }
        }
    }
}

private struct StatusGallery: View {
    var body: some View {
        PreviewCanvas {
            HStack { DS.StatusPill(state: .ready); DS.StatusPill(state: .connecting) }
            HStack { DS.StatusPill(state: .offline); DS.StatusPill(state: .limited) }
            ProBadge()
            DS.TileRow {
                MetricGauge(title: "CPU", value: 34, text: "34 %")
                MetricGauge(title: "GPU", value: nil, text: "—")
                MetricGauge(title: "RAM", value: nil, text: "", isLoading: true)
            }
            Toast("Saved", kind: .success) {}
            Toast("Something failed", kind: .error) {}
            Toast("Info") {}
            GlassCard { Text(verbatim: "GlassCard").foregroundStyle(Theme.Palette.textPrimary) }
            GlassCard(isHighlighted: true) { Text(verbatim: "Highlighted").foregroundStyle(Theme.Palette.textPrimary) }
            GlassCard { Text(verbatim: "Disabled").foregroundStyle(Theme.Palette.textPrimary) }.disabled(true)
            NavRow(title: "permission.name.rgb", systemImage: "lightbulb.fill")
            NavRow(title: "permission.name.rgb", systemImage: "lightbulb.fill", badge: .pro)
            NavRow(title: "permission.name.input", systemImage: "cursorarrow", badge: .permissionNeeded)
        }
    }
}

private struct StatesGallery: View {
    var body: some View {
        PreviewCanvas {
            EmptyState.noPC {}
            LoadingState()
            LoadingState(startSlow: true) {}
            ErrorCard(message: "The PC did not respond.") {}
            ErrorCard(message: "The PC did not respond.", isRetrying: true) {}
            OfflineCard(onRetry: {}, onWake: {})
            OfflineCard(isRetrying: true, onRetry: {})
            PermissionCard(permission: .input) {}
            PermissionCard(permission: .files, isChecking: true) {}
            TrayMissingCard()
            ProCard(feature: "permission.name.rgb") {}
            ProCard(feature: "permission.name.rgb", isStoreReachable: false) {}
            LimitNotice {}
        }
    }
}

#Preview("Buttons") { ButtonsGallery() }
#Preview("Buttons · AX5") { ButtonsGallery().environment(\.dynamicTypeSize, .accessibility5) }
#Preview("Buttons · Reduce Motion") { ButtonsGallery().designAccessibility(reduceMotion: true) }
#Preview("Status") { StatusGallery() }
#Preview("Status · AX5") { StatusGallery().environment(\.dynamicTypeSize, .accessibility5) }
#Preview("Status · Reduce Motion") { StatusGallery().designAccessibility(reduceMotion: true) }
#Preview("Status · Reduce Transparency + Contrast") {
    StatusGallery().designAccessibility(reduceTransparency: true, increasedContrast: true)
}
#Preview("States") { StatesGallery() }
#Preview("States · AX5") { StatesGallery().environment(\.dynamicTypeSize, .accessibility5) }
#Preview("States · Reduce Motion") { StatesGallery().designAccessibility(reduceMotion: true) }
#Preview("States · RU") { StatesGallery().environment(\.locale, Locale(identifier: "ru")) }
#endif
