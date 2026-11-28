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

/// What a section shows (ios-screens.md §2, steps 1–6). The decision is `SectionAccess` in NexusKit
/// (unit-tested); here only the permission is mapped to the app's `PermissionName`.
enum SectionState: Equatable, Sendable {
    case noPC
    case offline
    case connecting
    case permission(PermissionName)
    case trayMissing
    case pro(EntitlementState?)
    case content

    init(_ access: SectionAccess, permission: PermissionName) {
        switch access {
        case .noPC: self = .noPC
        case .offline: self = .offline
        case .connecting: self = .connecting
        case .permission: self = .permission(permission)
        case .trayMissing: self = .trayMissing
        case let .pro(state): self = .pro(state)
        case .content: self = .content
        }
    }
}

/// Shows the section itself, or the empty / offline / connecting / permission / tray / Pro state in its place.
/// The row that leads here is never hidden.
struct SectionGate<Content: View>: View {
    let controller: PCController
    let title: LocalizedStringResource
    let commands: [String]
    let permission: PermissionName
    /// The section needs the tray app on the PC (`pc.status.userSession == available`).
    let needsTray: Bool
    /// Pro feature id (docs/API.md, «Планы»); `nil` for Free sections.
    let requiredFeature: String?
    let onConnect: () -> Void
    private let content: () -> Content

    init(controller: PCController, title: LocalizedStringResource, commands: [String], permission: PermissionName,
         needsTray: Bool = false, requiredFeature: String? = nil,
         onConnect: @escaping () -> Void, @ViewBuilder content: @escaping () -> Content) {
        self.controller = controller
        self.title = title
        self.commands = commands
        self.permission = permission
        self.needsTray = needsTray
        self.requiredFeature = requiredFeature
        self.onConnect = onConnect
        self.content = content
    }

    private var state: SectionState {
        let connection: SectionAccess.Connection = switch controller.state {
        case .online: .online
        case .connecting: .connecting
        case .offline, .notPaired: .offline
        }
        let access = SectionAccess.resolve(
            isPaired: controller.pc != nil, connection: connection, capabilities: controller.capabilities,
            anyOf: commands, userSessionAvailable: controller.status?.userSessionAvailable ?? false,
            needsTray: needsTray, hello: controller.hello, requiredFeature: requiredFeature)
        return SectionState(access, permission: permission)
    }

    /// `pro.feature.<feature>.body`, or the expired / revoked text.
    private func proText(_ entitlement: EntitlementState?) -> LocalizedStringResource {
        switch entitlement {
        case .expired: "pro.expired.body"
        case .revoked: "pro.revoked.body"
        default: LocalizedStringResource(stringLiteral: "pro.feature.\(requiredFeature ?? "").body")
        }
    }

    var body: some View {
        switch state {
        case .content:
            content()
        case .noPC:
            placeholder { EmptyState.noPC(action: onConnect) }
        case .connecting:
            placeholder { LoadingState() }
        case .trayMissing:
            placeholder { TrayMissingCard() }
        case let .pro(entitlement):
            placeholder {
                // TODO(E6-SCREENS-2 / E6-ACC): open the Pro screen (A10) once it exists.
                ProCard(feature: proText(entitlement)) {}
            }
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
