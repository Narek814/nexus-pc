import NexusKit
import SwiftUI
import UIKit

/// Keyboard (Control → Keyboard): live typing to the PC plus the keys the iPhone keyboard lacks.
/// Built from existing parts only: `LiveTypingField` (touchpad typing) and `ScreenKeysRow`.
struct KeyboardView: View {
    let controller: PCController

    @State private var sender: InputSender
    @State private var typing = false

    init(controller: PCController) {
        self.controller = controller
        _sender = State(initialValue: InputSender(controller: controller))
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            VStack(alignment: .leading, spacing: Theme.Space.l) {
                if let message = sender.message {
                    Text(verbatim: message)
                        .font(.footnote)
                        .foregroundStyle(Theme.Palette.stateWarning)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
                Text("keyboard.hint")
                    .font(Theme.Typography.callout)
                    .foregroundStyle(Theme.Palette.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                NeonButton(title: typing ? LocalizedStringResource("keyboard.hide") : LocalizedStringResource("keyboard.show"),
                           systemImage: typing ? "keyboard.chevron.compact.down" : "keyboard") {
                    typing.toggle()
                }
                ScreenKeysRow(sender: sender)
                LiveTypingField(typing: typing, sender: sender)
                    .frame(width: 1, height: 1)
                    .accessibilityHidden(true)
                Spacer(minLength: 0)
            }
            .padding(Theme.Space.xl)
        }
        .navigationTitle(Text("section.keyboard"))
        .navigationBarTitleDisplayMode(.inline)
        .onDisappear { typing = false }
    }
}
