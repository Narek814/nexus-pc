import SwiftUI

/// PC tab root: PC Monitor, RGB Lighting, Files (clipboard stays inside Files).
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
                        row("section.rgb", "light.max", ["rgb.status"], .rgb) { RgbView(controller: controller) }
                        row("section.files", "arrow.up.arrow.down.square.fill", ["files.list", "clipboard.get"], .files) { FilesView(controller: controller) }
                    }
                    .padding(Theme.Space.xl)
                }
                .refreshable { await controller.refresh() }
            }
            .navigationTitle(Text("tab.pc"))
        }
    }

    private func row<Destination: View>(_ title: LocalizedStringResource, _ icon: String, _ commands: [String], _ permission: PermissionName,
                                        @ViewBuilder destination: @escaping () -> Destination) -> some View {
        NavigationLink {
            SectionGate(controller: controller, title: title, commands: commands, permission: permission,
                        onConnect: onConnect, content: destination)
        } label: {
            NavRow(title: title, systemImage: icon)
        }
        .buttonStyle(PressableButtonStyle())
    }
}
