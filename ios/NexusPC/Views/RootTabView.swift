import NexusKit
import SwiftUI

/// The five tabs (docs/design/navigation.md §2). Owns the single `PCController`, refresh,
/// the pairing sheet, deep links and the banner toast, so they work from any tab.
struct RootTabView: View {
    enum RootTab: Hashable {
        case home, control, pc, automations, more
    }

    @State private var controller = PCController()
    @State private var selection: RootTab = .home
    @State private var showingPairing = false
    @State private var pairingLink: PairingLink?
    @State private var confirmPower: HomeView.PowerAction?
    @Environment(\.scenePhase) private var scenePhase
    private var a11y = DesignAccessibility()

    init() {}

    var body: some View {
        TabView(selection: $selection) {
            NavigationStack {
                HomeView(controller: controller, confirmPower: $confirmPower, onConnect: { openPairing() })
            }
            .tabItem { Label("tab.home", systemImage: "house.fill") }
            .tag(RootTab.home)

            NavigationStack {
                ControlHomeView(controller: controller, onConnect: { openPairing() })
            }
            .tabItem { Label("tab.control", systemImage: "hand.point.up.left.fill") }
            .tag(RootTab.control)

            NavigationStack {
                PCHomeView(controller: controller, onConnect: { openPairing() })
            }
            .tabItem { Label("tab.pc", systemImage: "desktopcomputer") }
            .tag(RootTab.pc)

            NavigationStack {
                AutomationsRootView(controller: controller, onConnect: { openPairing() })
            }
            .tabItem { Label("tab.automations", systemImage: "gearshape.2.fill") }
            .tag(RootTab.automations)

            NavigationStack {
                MoreView(controller: controller, onConnect: { openPairing() })
            }
            .tabItem { Label("tab.more", systemImage: "ellipsis.circle.fill") }
            .tag(RootTab.more)
        }
        .tint(Theme.Palette.accentSecondary)
        .overlay(alignment: .top) {
            if let banner = controller.banner {
                Toast(banner) { controller.banner = nil }
                    .padding(.horizontal, Theme.Space.l)
                    .transition(Toast.transition(reduceMotion: a11y.reduceMotion))
            }
        }
        .animation(Theme.Motion.standard(reduceMotion: a11y.reduceMotion), value: controller.banner)
        .sheet(isPresented: $showingPairing, onDismiss: { pairingLink = nil }) {
            PairingFlowView(initialLink: pairingLink) { controller.paired($0) }
        }
        .onOpenURL { handle($0) }
        .task { await controller.refresh() }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active {
                Task { await controller.refresh() }
            }
        }
    }

    /// Opens the pairing sheet; `link == nil` starts with the QR scanner (also used to update the PC address).
    private func openPairing(link: PairingLink? = nil) {
        pairingLink = link
        showingPairing = true
    }

    // MARK: - Links from widgets, Live Activities and pairing QR codes

    private func handle(_ url: URL) {
        guard url.scheme == "nexuspc" else {
            return
        }
        if url.host == "pair" {
            if let link = PairingLink(url.absoluteString) {
                openPairing(link: link)
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
            selection = .home
            confirmPower = .shutdown // never without confirmation: HomeView shows the dialog
        case "cancel":
            Task { await controller.perform(.cancel) }
        case "profile" where parts.count > 1:
            Task { await controller.runProfile(id: parts[1]) }
        default:
            break
        }
    }
}

// MARK: - Section state

/// What a section shows (navigation.md: offline → permission → content). Pure, so it can be
/// unit-tested once the app gets a test target (NexusPC has none yet).
enum SectionState: Equatable, Sendable {
    case noPC
    case offline
    case permission(PermissionName)
    case content

    /// - Parameters:
    ///   - anyOf: the section is available if the PC allows at least one of these commands.
    static func resolve(isPaired: Bool, isOnline: Bool, capabilities: Set<String>,
                        anyOf commands: [String], permission: PermissionName) -> SectionState {
        if !isPaired {
            return .noPC
        }
        if !isOnline, capabilities.isEmpty {
            return .offline
        }
        if !commands.contains(where: { capabilities.contains($0) }) {
            return .permission(permission)
        }
        return .content
    }
}

/// Shows the section itself, or the empty / offline / permission state in its place.
/// The row that leads here is never hidden.
struct SectionGate<Content: View>: View {
    let controller: PCController
    let title: LocalizedStringResource
    let commands: [String]
    let permission: PermissionName
    let onConnect: () -> Void
    private let content: () -> Content

    init(controller: PCController, title: LocalizedStringResource, commands: [String], permission: PermissionName,
         onConnect: @escaping () -> Void, @ViewBuilder content: @escaping () -> Content) {
        self.controller = controller
        self.title = title
        self.commands = commands
        self.permission = permission
        self.onConnect = onConnect
        self.content = content
    }

    private var state: SectionState {
        SectionState.resolve(isPaired: controller.pc != nil, isOnline: controller.state == .online,
                             capabilities: controller.capabilities, anyOf: commands, permission: permission)
    }

    var body: some View {
        switch state {
        case .content:
            content()
        case .noPC:
            placeholder { EmptyState.noPC(action: onConnect) }
        case .offline:
            placeholder {
                OfflineCard(
                    isRetrying: controller.state == .connecting,
                    onRetry: { Task { await controller.refresh() } },
                    onWake: controller.canWake ? { Task { await controller.wake() } } : nil)
            }
        case let .permission(name):
            placeholder {
                PermissionCard(permission: name, isChecking: controller.state == .connecting) {
                    Task { await controller.refresh() }
                }
            }
        }
    }

    private func placeholder<P: View>(@ViewBuilder _ view: () -> P) -> some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                view().padding(Theme.Space.xl)
            }
            .refreshable { await controller.refresh() }
        }
        .navigationTitle(Text(title))
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// Root of a tab with no PC paired: the "No PC Connected" empty state.
struct NoPCRoot: View {
    let title: LocalizedStringResource
    let onConnect: () -> Void

    init(title: LocalizedStringResource, onConnect: @escaping () -> Void) {
        self.title = title
        self.onConnect = onConnect
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                EmptyState.noPC(action: onConnect).padding(.top, Theme.Space.xxl)
            }
        }
        .navigationTitle(Text(title))
    }
}

// MARK: - Automations tab

/// Automations tab root: the automations list, with History one row above it.
struct AutomationsRootView: View {
    let controller: PCController
    let onConnect: () -> Void

    init(controller: PCController, onConnect: @escaping () -> Void) {
        self.controller = controller
        self.onConnect = onConnect
    }

    var body: some View {
        if controller.pc == nil {
            NoPCRoot(title: "tab.automations", onConnect: onConnect)
        } else {
            SectionGate(controller: controller, title: "section.automations", commands: ["automations.list"],
                        permission: .automation, onConnect: onConnect) {
                AutomationsView(controller: controller)
            }
            .safeAreaInset(edge: .top) {
                NavigationLink {
                    // The agent checks history.list against the "monitoring" permission (CommandCatalog.cs).
                    SectionGate(controller: controller, title: "section.history", commands: ["history.list"],
                                permission: .monitoring, onConnect: onConnect) {
                        HistoryView(controller: controller)
                    }
                } label: {
                    NavRow(title: "section.history", systemImage: "clock.arrow.circlepath")
                }
                .buttonStyle(PressableButtonStyle())
                .padding(.horizontal, Theme.Space.xl)
                .padding(.vertical, Theme.Space.s)
                .background(Theme.background)
            }
        }
    }
}
