import SwiftUI

/// Pro has ended (`expired` / `revoked`): the rules are stopped, not deleted (docs/design/ios-screens.md, 3.2 and 9).
/// Owner's decision: no "Learn About Pro" button until the Pro screen exists.
struct AutomationsPausedBanner: View {
    init() {}

    var body: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: Theme.Space.m) {
                Label {
                    Text("automations.paused.title")
                        .font(Theme.Typography.headline)
                        .foregroundStyle(Theme.Palette.textPrimary)
                        .lineLimit(nil)
                        .fixedSize(horizontal: false, vertical: true)
                } icon: {
                    Image(systemName: "pause.circle.fill")
                        .foregroundStyle(Theme.Palette.stateWarning)
                        .accessibilityHidden(true)
                }
                .accessibilityAddTraits(.isHeader)
                Text("automations.paused.body")
                    .font(Theme.Typography.callout)
                    .foregroundStyle(Theme.Palette.textSecondary)
                    .lineLimit(nil)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .overlay {
            RoundedRectangle(cornerRadius: Theme.Radius.card, style: .continuous)
                .strokeBorder(Theme.Palette.stateWarning, lineWidth: Theme.Size.strokeCard)
        }
        .accessibilityElement(children: .combine)
    }
}
