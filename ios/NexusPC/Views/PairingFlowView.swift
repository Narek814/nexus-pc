import NexusKit
import SwiftUI
import UIKit

struct PairingFlowView: View {
    let onPaired: (PairedPC) -> Void
    @State private var model: PairingFlowModel

    init(initialLink: PairingLink? = nil, onPaired: @escaping (PairedPC) -> Void) {
        self.onPaired = onPaired
        _model = State(initialValue: PairingFlowModel(initialLink: initialLink))
    }
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ZStack {
                Theme.background.ignoresSafeArea()
                content.padding(20)
            }
            .navigationTitle("Подключить ПК")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Закрыть") { dismiss() }
                }
            }
        }
    }

    @ViewBuilder
    private var content: some View {
        switch model.step {
        case let .confirm(link):
            VStack(spacing: 20) {
                StatusCard(icon: "link", title: "Подключить \(link.pcName)?", text: "Адреса: \(link.hosts.joined(separator: ", ")). Подключайтесь, только если сами открыли это окно на своём ПК.", busy: false)
                NeonButton(title: "Подключить", systemImage: "checkmark") { model.confirm(link) }
                Spacer()
            }
        case .scanning:
            VStack(spacing: 16) {
                Text("На ПК: значок Nexus PC у часов → «Подключить iPhone…». Наведите камеру на QR-код.")
                    .font(.subheadline)
                    .foregroundStyle(Theme.textMuted)
                if QRScannerView.isAvailable {
                    QRScannerView { model.handle(scanned: $0) }
                        .clipShape(RoundedRectangle(cornerRadius: Theme.cornerRadius, style: .continuous))
                        .overlay {
                            RoundedRectangle(cornerRadius: Theme.cornerRadius, style: .continuous)
                                .strokeBorder(Theme.accent, lineWidth: 2)
                        }
                } else {
                    GlassCard {
                        Label("Камера недоступна. Скопируйте ссылку на ПК («Копировать ссылку») и вставьте её.", systemImage: "camera.metering.unknown")
                            .foregroundStyle(Theme.textPrimary)
                    }
                    Spacer()
                }
                Button {
                    model.handle(scanned: UIPasteboard.general.string ?? "")
                } label: {
                    Label("Вставить ссылку", systemImage: "doc.on.clipboard")
                }
                .foregroundStyle(Theme.cyan)
            }
        case let .connecting(link):
            StatusCard(icon: "antenna.radiowaves.left.and.right", title: "Подключаюсь к \(link.pcName)…", text: link.hosts.joined(separator: ", "), busy: true)
        case let .waitingForApproval(link, code):
            VStack(spacing: 20) {
                StatusCard(icon: "checkmark.shield", title: "Подтвердите на ПК", text: "На экране \(link.pcName) появился запрос. Нажмите «Подключить», если код совпадает.", busy: true)
                Text(code)
                    .font(.system(size: 48, weight: .bold, design: .monospaced))
                    .foregroundStyle(Theme.accent)
                    .accessibilityLabel("Код проверки \(code.map(String.init).joined(separator: " "))")
                Spacer()
            }
        case let .done(pc):
            VStack(spacing: 20) {
                StatusCard(icon: "checkmark.seal.fill", title: "\(pc.name) подключён", text: "Ключ этого iPhone хранится в Secure Enclave. Разрешения: \(pc.permissions.joined(separator: ", ")).", busy: false)
                NeonButton(title: "Готово", systemImage: "arrow.right") {
                    onPaired(pc)
                    dismiss()
                }
                Spacer()
            }
        case let .failed(message):
            VStack(spacing: 20) {
                StatusCard(icon: "xmark.octagon", title: "Не получилось", text: message, busy: false)
                NeonButton(title: "Попробовать снова", systemImage: "arrow.clockwise") { model.retry() }
                Spacer()
            }
        }
    }
}

struct StatusCard: View {
    let icon: String
    let title: String
    let text: String
    let busy: Bool

    var body: some View {
        GlassCard {
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: icon)
                    .font(.title2)
                    .foregroundStyle(Theme.cyan)
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(title).font(.headline).foregroundStyle(Theme.textPrimary)
                        if busy {
                            ProgressView().tint(Theme.cyan)
                        }
                    }
                    Text(text).font(.subheadline).foregroundStyle(Theme.textMuted)
                }
            }
        }
    }
}
