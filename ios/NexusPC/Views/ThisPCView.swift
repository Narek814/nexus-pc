import NexusKit
import SwiftUI

/// "This PC" (More → This PC): this iPhone's key fingerprint, update the PC address by QR, forget the PC.
/// Moved unchanged from the old Home screen. TEMPORARY: in E6-ACC it moves to Account → My Devices.
struct ThisPCView: View {
    let controller: PCController
    /// Opens the pairing sheet with the QR scanner (no link), as Home did before.
    let onUpdateAddress: () -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var confirmingForget = false

    init(controller: PCController, onUpdateAddress: @escaping () -> Void) {
        self.controller = controller
        self.onUpdateAddress = onUpdateAddress
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(spacing: Theme.Space.l) {
                    if let name = controller.status?.name ?? controller.pc?.name {
                        Text(verbatim: name)
                            .font(Theme.Typography.title)
                            .foregroundStyle(Theme.Palette.textPrimary)
                    }
                    // What the administrator types in "automations assign" on the PC (T3), next to what "devices" shows.
                    if let fingerprint = controller.deviceKeyFingerprint {
                        VStack(spacing: 2) {
                            Text("pc.keyFingerprint")
                                .font(.caption2)
                                .foregroundStyle(Theme.textMuted)
                            Text(verbatim: DeviceCrypto.grouped(fingerprint))
                                .font(.footnote.monospaced())
                                .foregroundStyle(Theme.textPrimary)
                                .textSelection(.enabled)
                        }
                        .accessibilityElement(children: .combine)
                    }
                    // If the PC moved to another address and was not found automatically: scan its QR again.
                    Button(action: onUpdateAddress) {
                        Label("pc.updateAddressByQR", systemImage: "qrcode.viewfinder")
                            .font(.footnote)
                    }
                    .tint(Theme.cyan)
                    // Never without confirmation: the pairing is removed only on this iPhone.
                    Button(role: .destructive) {
                        confirmingForget = true
                    } label: {
                        Label("pc.forget", systemImage: "trash")
                            .font(.footnote)
                    }
                    .confirmationDialog(Text("pc.forget.confirm.title"), isPresented: $confirmingForget, titleVisibility: .visible) {
                        Button(role: .destructive) {
                            controller.forget()
                            dismiss()
                        } label: {
                            Text("pc.forget.confirm.action")
                        }
                        Button(role: .cancel) {} label: {
                            Text("common.cancel")
                        }
                    } message: {
                        Text("pc.forget.confirm.message")
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(Theme.Space.xl)
            }
        }
        .navigationTitle(Text("section.thisPC"))
        .navigationBarTitleDisplayMode(.inline)
    }
}
