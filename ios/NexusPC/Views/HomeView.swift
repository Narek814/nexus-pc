import NexusKit
import SwiftUI

/// Command Center (P2): PC status, power, apps and profiles.
struct HomeView: View {
    @State private var controller = PCController()
    @State private var showingPairing = false
    @State private var pairingLink: PairingLink?
    @State private var confirmPower: PowerAction?
    @Environment(\.scenePhase) private var scenePhase

    enum PowerAction: String, Identifiable {
        case shutdown, restart, sleep
        var id: String { rawValue }
    }

    var body: some View {
        NavigationStack {
            content
                .toolbar(.hidden, for: .navigationBar)
        }
    }

    private var content: some View {
        ZStack(alignment: .top) {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    header
                    if controller.pc == nil {
                        onboarding
                    } else {
                        statusCard
                        if controller.can("metrics.snapshot") {
                            monitoringLink
                        }
                        if controller.can("alerts.get") {
                            NavigationLink {
                                AlertsView(controller: controller)
                            } label: {
                                GlassCard {
                                    HStack {
                                        Label("Уведомления и предупреждения", systemImage: "bell.badge.fill")
                                            .foregroundStyle(Theme.textPrimary)
                                        Spacer()
                                        Image(systemName: "chevron.right").foregroundStyle(Theme.textMuted)
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                        }
                        powerGrid
                        if let pending = controller.pendingPower {
                            GlassCard {
                                HStack {
                                    Label(pending, systemImage: "timer").foregroundStyle(Theme.textPrimary)
                                    Spacer()
                                    Button("Отменить") { Task { await controller.perform(.cancel) } }
                                        .foregroundStyle(Theme.cyan)
                                }
                            }
                        }
                        profilesSection
                        appsSection
                        forgetButton
                    }
                }
                .padding(20)
            }
            .refreshable { await controller.refresh() }

            if let banner = controller.banner {
                Text(banner)
                    .font(.subheadline.weight(.semibold))
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .background(Theme.surface, in: Capsule())
                    .overlay(Capsule().strokeBorder(Theme.accent, lineWidth: 1))
                    .foregroundStyle(Theme.textPrimary)
                    .padding(.top, 8)
                    .transition(.move(edge: .top).combined(with: .opacity))
                    .task(id: banner) {
                        try? await Task.sleep(nanoseconds: 3_000_000_000)
                        controller.banner = nil
                    }
            }
        }
        .animation(.easeInOut, value: controller.banner)
        .sheet(isPresented: $showingPairing, onDismiss: { pairingLink = nil }) {
            PairingFlowView(initialLink: pairingLink) { controller.paired($0) }
        }
        .onOpenURL { handle($0) }
        .confirmationDialog(confirmTitle, isPresented: Binding(get: { confirmPower != nil }, set: { if !$0 { confirmPower = nil } }), titleVisibility: .visible) {
            powerChoices
        }
        .task { await controller.refresh() }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active {
                Task { await controller.refresh() }
            }
        }
    }

    // MARK: - Sections

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("NEXUS PC")
                .font(.system(size: 34, weight: .heavy, design: .rounded))
                .foregroundStyle(Theme.accent)
            Text("Command Center")
                .font(.headline)
                .foregroundStyle(Theme.textMuted)
        }
        .padding(.top, 12)
    }

    private var onboarding: some View {
        VStack(spacing: 20) {
            GlassCard {
                VStack(alignment: .leading, spacing: 8) {
                    Label("ПК не подключён", systemImage: "desktopcomputer")
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(Theme.textPrimary)
                    Text("На ПК нажмите на значок Nexus PC у часов → «Подключить iPhone…», затем отсканируйте QR-код.")
                        .font(.subheadline)
                        .foregroundStyle(Theme.textMuted)
                }
            }
            NeonButton(title: "Подключить ПК", systemImage: "qrcode.viewfinder") { showingPairing = true }
        }
    }

    private var statusCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Image(systemName: "desktopcomputer").font(.title2).foregroundStyle(Theme.cyan)
                    Text(controller.status?.name ?? controller.pc?.name ?? "ПК")
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(Theme.textPrimary)
                    Spacer()
                    StatusPill(state: controller.state)
                }
                if let status = controller.status, controller.state == .online {
                    Text("Работает \(Self.uptime(status.uptimeSeconds)) · \(status.os)")
                        .font(.footnote)
                        .foregroundStyle(Theme.textMuted)
                    if !status.userSessionAvailable {
                        Label("Nexus PC Tray не запущен — программы и блокировка недоступны.", systemImage: "exclamationmark.triangle")
                            .font(.footnote)
                            .foregroundStyle(.orange)
                    }
                } else if case let .offline(reason) = controller.state {
                    Text(reason).font(.footnote).foregroundStyle(Theme.textMuted)
                    if controller.canWake {
                        NeonButton(title: "Включить ПК", systemImage: "power.circle") {
                            Task { await controller.wake() }
                        }
                        .padding(.top, 4)
                    }
                }
            }
        }
    }

    private var monitoringLink: some View {
        NavigationLink {
            MonitoringView(controller: controller)
        } label: {
            GlassCard {
                HStack(spacing: 16) {
                    Image(systemName: "waveform.path.ecg").font(.title2).foregroundStyle(Theme.accent)
                    metricSummary("CPU", Format.percent(controller.metrics?.cpuUsage))
                    metricSummary("GPU", Format.percent(controller.metrics?.gpus.first?.usage))
                    metricSummary("RAM", Format.percent(controller.metrics?.memoryUsage))
                    metricSummary("GPU", Format.temperature(controller.metrics?.gpus.first?.temperature))
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right").foregroundStyle(Theme.textMuted)
                }
            }
        }
        .buttonStyle(.plain)
    }

    private func metricSummary(_ title: String, _ value: String) -> some View {
        VStack(spacing: 2) {
            Text(value).font(.subheadline.weight(.bold)).monospacedDigit().foregroundStyle(Theme.textPrimary)
            Text(title).font(.caption2).foregroundStyle(Theme.textMuted)
        }
    }

    private var powerGrid: some View {
        let online = controller.state == .online
        return LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 12) {
            PowerTile(title: "Выключить", icon: "power", tint: .red, enabled: online && controller.can("power.shutdown")) { confirmPower = .shutdown }
            PowerTile(title: "Перезагрузить", icon: "arrow.clockwise", tint: Theme.violet, enabled: online && controller.can("power.restart")) { confirmPower = .restart }
            PowerTile(title: "Сон", icon: "moon.fill", tint: Theme.cyan, enabled: online && controller.can("power.sleep")) { confirmPower = .sleep }
            PowerTile(title: "Заблокировать", icon: "lock.fill", tint: Theme.cyan, enabled: online && controller.can("power.lock")) {
                Task { await controller.perform(.lock) }
            }
        }
    }

    @ViewBuilder
    private var profilesSection: some View {
        if !controller.profiles.isEmpty {
            SectionTitle("Профили")
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 12) {
                    ForEach(controller.profiles) { profile in
                        Button {
                            Task { await controller.runProfile(profile) }
                        } label: {
                            VStack(alignment: .leading, spacing: 6) {
                                Image(systemName: Self.icon(for: profile.id)).font(.title2).foregroundStyle(Theme.accent)
                                Text(profile.name).font(.headline).foregroundStyle(Theme.textPrimary)
                                Text("\(profile.appCount) прил.").font(.caption).foregroundStyle(Theme.textMuted)
                            }
                            .frame(width: 130, alignment: .leading)
                            .padding(14)
                            .background(Theme.surface, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                            .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Theme.accent, lineWidth: 1).opacity(0.5))
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var appsSection: some View {
        if !controller.apps.isEmpty {
            SectionTitle("Приложения")
            VStack(spacing: 8) {
                ForEach(controller.apps) { app in
                    Button {
                        Task { await controller.launch(app) }
                    } label: {
                        HStack {
                            Image(systemName: "app.badge.fill").foregroundStyle(Theme.cyan)
                            Text(app.name).foregroundStyle(Theme.textPrimary)
                            Spacer()
                            Image(systemName: "play.fill").foregroundStyle(Theme.violet)
                        }
                        .padding(14)
                        .background(Theme.surface, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private var forgetButton: some View {
        Button(role: .destructive) {
            controller.forget()
        } label: {
            Label("Забыть этот ПК", systemImage: "trash")
                .font(.footnote)
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
    }

    // MARK: - Power confirmation

    private var confirmTitle: String {
        switch confirmPower {
        case .shutdown: return "Выключить \(controller.status?.name ?? "ПК")?"
        case .restart: return "Перезагрузить \(controller.status?.name ?? "ПК")?"
        case .sleep: return "Перевести ПК в сон?"
        case nil: return ""
        }
    }

    @ViewBuilder
    private var powerChoices: some View {
        switch confirmPower {
        case .shutdown:
            Button("Выключить сейчас", role: .destructive) { Task { await controller.perform(.shutdown(delaySeconds: 0)) } }
            Button("Через 10 минут") { Task { await controller.perform(.shutdown(delaySeconds: 600)) } }
            Button("Через 30 минут") { Task { await controller.perform(.shutdown(delaySeconds: 1800)) } }
        case .restart:
            Button("Перезагрузить сейчас", role: .destructive) { Task { await controller.perform(.restart(delaySeconds: 0)) } }
        case .sleep:
            Button("Сон") { Task { await controller.perform(.sleep) } }
        case nil:
            EmptyView()
        }
        Button("Отмена", role: .cancel) {}
    }

    // MARK: - Links from widgets, Live Activities and pairing QR codes

    private func handle(_ url: URL) {
        guard url.scheme == "nexuspc" else {
            return
        }
        if url.host == "pair" {
            if let link = PairingLink(url.absoluteString) {
                pairingLink = link
                showingPairing = true
            }
            return
        }
        guard url.host == "action", controller.pc != nil else {
            return
        }
        let parts = url.pathComponents.filter { $0 != "/" }
        switch parts.first ?? "" {
        case "lock":
            Task { await controller.perform(.lock) }
        case "shutdown":
            confirmPower = .shutdown // never without confirmation
        case "cancel":
            Task { await controller.perform(.cancel) }
        case "profile" where parts.count > 1:
            Task { await controller.runProfile(id: parts[1]) }
        default:
            break
        }
    }

    // MARK: - Helpers

    static func uptime(_ seconds: Int) -> String {
        let hours = seconds / 3600
        let minutes = seconds % 3600 / 60
        return hours > 0 ? "\(hours) ч \(minutes) мин" : "\(minutes) мин"
    }

    static func icon(for profileId: String) -> String {
        switch profileId {
        case "gaming": return "gamecontroller.fill"
        case "developer": return "chevron.left.forwardslash.chevron.right"
        case "creator": return "video.fill"
        case "night": return "moon.stars.fill"
        default: return "square.stack.3d.up.fill"
        }
    }
}

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

struct StatusPill: View {
    let state: PCController.ConnectionState

    var body: some View {
        let (text, color): (String, Color) = switch state {
        case .online: ("В сети", .green)
        case .connecting: ("Подключение", .yellow)
        case .offline: ("Нет связи", .gray)
        case .notPaired: ("—", .gray)
        }
        return HStack(spacing: 6) {
            Circle().fill(color).frame(width: 8, height: 8).shadow(color: color, radius: 4)
            Text(text).font(.caption.weight(.semibold)).foregroundStyle(Theme.textPrimary)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(Theme.surface, in: Capsule())
    }
}

struct PowerTile: View {
    let title: String
    let icon: String
    let tint: Color
    let enabled: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 10) {
                Image(systemName: icon).font(.title2)
                Text(title).font(.subheadline.weight(.semibold))
            }
            .foregroundStyle(enabled ? tint : Theme.textMuted)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 18)
            .background(Theme.surface, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(enabled ? tint : Theme.surface, lineWidth: 1).opacity(0.6))
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
    }
}
