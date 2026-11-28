import SwiftUI

/// More tab root. Only rows that work today: "This PC" when a PC is paired, otherwise "Connect PC".
/// Account, Settings and Help come with their own tasks (E6-ACC and later) — no placeholders here.
struct MoreView: View {
    let controller: PCController
    let onConnect: () -> Void

    init(controller: PCController, onConnect: @escaping () -> Void) {
        self.controller = controller
        self.onConnect = onConnect
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(spacing: Theme.Space.m) {
                    if controller.pc != nil {
                        NavigationLink {
                            ThisPCView(controller: controller, onUpdateAddress: onConnect)
                        } label: {
                            NavRow(title: "section.thisPC", systemImage: "desktopcomputer")
                        }
                        .buttonStyle(PressableButtonStyle())
                    } else {
                        Button(action: onConnect) {
                            NavRow(title: "empty.noPC.action", systemImage: "qrcode.viewfinder")
                        }
                        .buttonStyle(PressableButtonStyle())
                    }
                }
                .padding(Theme.Space.xl)
            }
        }
        .navigationTitle(Text("tab.more"))
    }
}
