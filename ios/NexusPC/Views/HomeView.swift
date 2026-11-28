import NexusKit
import SwiftUI

/// Home tab root: PC status, power, and previews of profiles and apps.
/// The power confirmation lives here only; deep links set `confirmPower` from RootTabView.
struct HomeView: View {
    let controller: PCController
    @Binding var confirmPower: PowerAction?
    let onConnect: () -> Void
    @State private var showingAllProfiles = false
    @State private var showingAllApps = false

    enum PowerAction: String, Identifiable {
        case shutdown, restart, sleep
        var id: String { rawValue }
    }

    private static let previewCount = 3

    init(controller: PCController, confirmPower: Binding<PowerAction?>, onConnect: @escaping () -> Void) {
        self.controller = controller
        _confirmPower = confirmPower
        self.onConnect = onConnect
    }

    var body: some View {
        Group {
            if controller.pc == nil {
                NoPCRoot(title: "tab.home", onConnect: onConnect)
            } else {
                content
            }
        }
        .confirmationDialog(confirmTitle, isPresented: Binding(get: { confirmPower != nil }, set: { if !$0 { confirmPower = nil } }), titleVisibility: .visible) {
            powerChoices
        }
        .navigationDestination(isPresented: $showingAllProfiles) { ProfilesView(controller: controller) }
        .navigationDestination(isPresented: $showingAllApps) { AppsView(controller: controller) }
    }

    private var pcName: String {
        controller.status?.name ?? controller.pc?.name ?? ""
    }

    private var content: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: Theme.Space.xl) {
                    statusCard
                    powerGrid
                    if let pending = controller.pendingPower {
                        GlassCard {
                            HStack {
                                Label(pending, systemImage: "timer").foregroundStyle(Theme.textPrimary)
                                Spacer()
                                Button("power.cancelTimer") { Task { await controller.perform(.cancel) } }
                                    .foregroundStyle(Theme.cyan)
                            }
                        }
                    }
                    profilesSection
                    appsSection
                }
                .padding(Theme.Space.xl)
            }
            .refreshable { await controller.refresh() }
        }
        .navigationTitle(Text(verbatim: pcName))
    }

    // MARK: - Sections

    private var pillState: StatusPillState {
        switch controller.state {
        case .online: controller.status?.userSessionAvailable == false ? .limited : .ready
        case .connecting: .connecting
        case .offline, .notPaired: .offline
        }
    }

    private var statusCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: Theme.Space.m) {
                HStack {
                    Image(systemName: "desktopcomputer").font(.title2).foregroundStyle(Theme.cyan)
                        .accessibilityHidden(true)
                    Text(verbatim: pcName)
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(Theme.textPrimary)
                    Spacer()
                    DS.StatusPill(state: pillState)
                }
                if let status = controller.status, controller.state == .online {
                    Text("home.uptime \(Self.uptime(status.uptimeSeconds)) \(status.os)")
                        .font(.footnote)
                        .foregroundStyle(Theme.textMuted)
                    if !status.userSessionAvailable {
                        TrayMissingCard()
                    }
                } else if case let .offline(reason) = controller.state {
                    Text(reason).font(.footnote).foregroundStyle(Theme.textMuted)
                    if controller.canWake {
                        NeonButton(title: "offline.wake", systemImage: "power.circle") {
                            Task { await controller.wake() }
                        }
                        .padding(.top, Theme.Space.xs)
                    }
                }
            }
        }
    }

    private var powerGrid: some View {
        let online = controller.state == .online
        return LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: Theme.Space.m) {
            DS.PowerTile(title: "power.shutdown", systemImage: "power", tint: Theme.Palette.stateDanger) { confirmPower = .shutdown }
                .disabled(!(online && controller.can("power.shutdown")))
            DS.PowerTile(title: "power.restart", systemImage: "arrow.clockwise", tint: Theme.violet) { confirmPower = .restart }
                .disabled(!(online && controller.can("power.restart")))
            DS.PowerTile(title: "power.sleep", systemImage: "moon.fill", tint: Theme.cyan) { confirmPower = .sleep }
                .disabled(!(online && controller.can("power.sleep")))
            DS.PowerTile(title: "power.lock", systemImage: "lock.fill", tint: Theme.cyan) {
                Task { await controller.perform(.lock) }
            }
            .disabled(!(online && controller.can("power.lock")))
        }
    }

    private var profilesSection: some View {
        VStack(alignment: .leading, spacing: Theme.Space.m) {
            SectionHeader("section.profiles", onSeeAll: { showingAllProfiles = true })
            if !controller.profiles.isEmpty {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: Theme.Space.m) {
                        ForEach(controller.profiles.prefix(Self.previewCount)) { profile in
                            ProfileCard(controller: controller, profile: profile)
                        }
                    }
                }
            }
        }
    }

    private var appsSection: some View {
        VStack(alignment: .leading, spacing: Theme.Space.m) {
            SectionHeader("section.apps", onSeeAll: { showingAllApps = true })
            VStack(spacing: Theme.Space.s) {
                ForEach(controller.apps.prefix(Self.previewCount)) { app in
                    AppRow(controller: controller, app: app)
                }
            }
        }
    }

    // MARK: - Power confirmation

    private var confirmTitle: String {
        switch confirmPower {
        case .shutdown: return String(localized: "power.confirm.shutdown \(pcName)")
        case .restart: return String(localized: "power.confirm.restart \(pcName)")
        case .sleep: return String(localized: "power.confirm.sleep")
        case nil: return ""
        }
    }

    @ViewBuilder
    private var powerChoices: some View {
        switch confirmPower {
        case .shutdown:
            Button("power.shutdown.now", role: .destructive) { Task { await controller.perform(.shutdown(delaySeconds: 0)) } }
            Button("power.in10min") { Task { await controller.perform(.shutdown(delaySeconds: 600)) } }
            Button("power.in30min") { Task { await controller.perform(.shutdown(delaySeconds: 1800)) } }
        case .restart:
            Button("power.restart.now", role: .destructive) { Task { await controller.perform(.restart(delaySeconds: 0)) } }
        case .sleep:
            Button("power.sleep") { Task { await controller.perform(.sleep) } }
        case nil:
            EmptyView()
        }
        Button("common.cancel", role: .cancel) {}
    }

    // MARK: - Helpers

    /// "3 h 5 min" / "3 ч 5 мин" in the user's language.
    static func uptime(_ seconds: Int) -> String {
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : [.minute]
        formatter.unitsStyle = .short
        return formatter.string(from: TimeInterval(seconds)) ?? ""
    }
}

/// Old screen-local section title; still used by MonitoringView, RgbView and FilesView
/// until E6-SCREENS moves them to `SectionHeader`.
struct SectionTitle: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text.uppercased())
            .font(.caption.weight(.bold))
            .tracking(1.5)
            .foregroundStyle(Theme.textMuted)
            .padding(.top, 4)
    }
}
