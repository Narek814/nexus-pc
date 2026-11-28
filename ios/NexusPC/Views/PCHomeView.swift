import NexusKit
import SwiftUI

/// PC tab root: PC Monitor, RGB Lighting, Files (clipboard stays inside Files).
/// RGB is Free for the device list (`rgb.status`), so the section is never gated by plan;
/// the row only gets `ProBadge` when `SectionAccess` says `rgb.sync` needs Pro.
struct PCHomeView: View {
    let controller: PCController
    let onConnect: () -> Void

    init(controller: PCController, onConnect: @escaping () -> Void) {
        self.controller = controller
        self.onConnect = onConnect
    }

    var body: some View {
        if controller.pc == nil {
            NoPCRoot(title: "tab.pc", onConnect: onConnect)
        } else {
            ZStack {
                Theme.background.ignoresSafeArea()
                ScrollView {
                    VStack(spacing: Theme.Space.m) {
                        row("section.monitor", "waveform.path.ecg", ["metrics.snapshot"], .monitoring) { MonitoringView(controller: controller) }
                        row("section.rgb", "light.max", ["rgb.status"], .rgb, badgeFeature: ProFeature.rgbSync) { RgbView(controller: controller) }
                        row("section.files", "arrow.up.arrow.down.square.fill", ["files.list", "clipboard.get"], .files) { FilesView(controller: controller) }
                    }
                    .padding(Theme.Space.xl)
                }
                .refreshable { await controller.refresh() }
            }
            .navigationTitle(Text("tab.pc"))
        }
    }

    /// `.pro` only when the section itself is reachable and step 6 of `SectionAccess` answers Pro.
    private func badge(_ commands: [String], feature: String?) -> NavRowBadge {
        guard let feature else {
            return .none
        }
        let connection: SectionAccess.Connection = switch controller.state {
        case .online: .online
        case .connecting: .connecting
        case .offline, .notPaired: .offline
        }
        let access = SectionAccess.resolve(
            isPaired: controller.pc != nil, connection: connection, capabilities: controller.capabilities,
            anyOf: commands, userSessionAvailable: controller.status?.userSessionAvailable ?? false,
            needsTray: false, hello: controller.hello, requiredFeature: feature)
        if case .pro = access {
            return .pro
        }
        return .none
    }

    private func row<Destination: View>(_ title: LocalizedStringResource, _ icon: String, _ commands: [String], _ permission: PermissionName,
                                        badgeFeature: String? = nil,
                                        @ViewBuilder destination: @escaping () -> Destination) -> some View {
        NavigationLink {
            SectionGate(controller: controller, title: title, commands: commands, permission: permission,
                        onConnect: onConnect, content: destination)
        } label: {
            NavRow(title: title, systemImage: icon, badge: badge(commands, feature: badgeFeature))
        }
        .buttonStyle(PressableButtonStyle())
    }
}
