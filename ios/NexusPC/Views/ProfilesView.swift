import NexusKit
import SwiftUI

/// All profiles of the PC (Home → Profiles → See All). Tapping a profile runs it.
/// Free (agent with plans, no `profiles` feature): `ProCard` instead of the list.
struct ProfilesView: View {
    let controller: PCController

    init(controller: PCController) {
        self.controller = controller
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                if let lock = ProLock.check(controller.hello, feature: ProFeature.profiles) {
                    ProCard(feature: lock.text)
                        .padding(Theme.Space.xl)
                } else if controller.profiles.isEmpty {
                    EmptyState(systemImage: "square.stack.3d.up", title: "profiles.empty.title", message: "profiles.empty.body")
                } else {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: Theme.Space.m)], spacing: Theme.Space.m) {
                        ForEach(controller.profiles) { profile in
                            ProfileCard(controller: controller, profile: profile)
                        }
                    }
                    .padding(Theme.Space.xl)
                }
            }
            .refreshable { await controller.refresh() }
        }
        .navigationTitle(Text("section.profiles"))
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// One profile tile: icon, name, number of apps. Runs the profile on tap.
struct ProfileCard: View {
    let controller: PCController
    let profile: RemoteProfile

    init(controller: PCController, profile: RemoteProfile) {
        self.controller = controller
        self.profile = profile
    }

    var body: some View {
        Button {
            Task { await controller.runProfile(profile) }
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                Image(systemName: Self.icon(for: profile.id)).font(.title2).foregroundStyle(Theme.accent)
                    .accessibilityHidden(true)
                Text(verbatim: profile.name).font(.headline).foregroundStyle(Theme.textPrimary)
                Text("profiles.appCount \(profile.appCount)").font(.caption).foregroundStyle(Theme.textMuted)
            }
            .frame(minWidth: 130, maxWidth: .infinity, alignment: .leading)
            .padding(14)
            .background(Theme.surface, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Theme.accent, lineWidth: 1).opacity(0.5))
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
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
