import SwiftUI

/// Control tab root: Screen, Touchpad, Keyboard. Rows are always shown; the permission
/// state appears inside the section (SectionGate).
struct ControlHomeView: View {
    let controller: PCController
    let onConnect: () -> Void

    init(controller: PCController, onConnect: @escaping () -> Void) {
        self.controller = controller
        self.onConnect = onConnect
    }

    var body: some View {
        if controller.pc == nil {
            NoPCRoot(title: "tab.control", onConnect: onConnect)
        } else {
            ZStack {
                Theme.background.ignoresSafeArea()
                ScrollView {
                    VStack(spacing: Theme.Space.m) {
                        row("section.screen", "display", ["screen.start"], .screen) { ScreenView(controller: controller) }
                        row("section.touchpad", "hand.point.up.left.fill", ["input.move"], .input) { TouchpadView(controller: controller) }
                        row("section.keyboard", "keyboard", ["input.move"], .input) { KeyboardView(controller: controller) }
                    }
                    .padding(Theme.Space.xl)
                }
                .refreshable { await controller.refresh() }
            }
            .navigationTitle(Text("tab.control"))
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
