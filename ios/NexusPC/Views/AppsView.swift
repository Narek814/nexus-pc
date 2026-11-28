import SwiftUI

/// All apps of the PC (Home → Apps → See All). Tapping an app launches it on the PC.
struct AppsView: View {
    let controller: PCController

    init(controller: PCController) {
        self.controller = controller
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                if controller.apps.isEmpty {
                    EmptyState(systemImage: "app.badge", title: "apps.empty.title", message: "apps.empty.body")
                } else {
                    VStack(spacing: Theme.Space.s) {
                        ForEach(controller.apps) { app in
                            AppRow(controller: controller, app: app)
                        }
                    }
                    .padding(Theme.Space.xl)
                }
            }
            .refreshable { await controller.refresh() }
        }
        .navigationTitle(Text("section.apps"))
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// One app row; launches the app on tap.
struct AppRow: View {
    let controller: PCController
    let app: RemoteApp

    init(controller: PCController, app: RemoteApp) {
        self.controller = controller
        self.app = app
    }

    var body: some View {
        Button {
            Task { await controller.launch(app) }
        } label: {
            HStack {
                Image(systemName: "app.badge.fill").foregroundStyle(Theme.cyan).accessibilityHidden(true)
                Text(verbatim: app.name).foregroundStyle(Theme.textPrimary)
                Spacer()
                Image(systemName: "play.fill").foregroundStyle(Theme.violet).accessibilityHidden(true)
            }
            .padding(14)
            .frame(minHeight: Theme.Size.hit)
            .background(Theme.surface, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
        .buttonStyle(.plain)
    }
}
