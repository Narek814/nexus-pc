import NexusKit
import SwiftUI
import UIKit

/// Command Center placeholder for P2.0: proves the app builds without a Mac, installs and runs.
/// It does not connect to a PC yet; pairing and the real Command Center are P2.
struct HomeView: View {
    @State private var showingPairing = false

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    header
                    GlassCard {
                        VStack(alignment: .leading, spacing: 8) {
                            Label("ПК не подключён", systemImage: "desktopcomputer")
                                .font(.title3.weight(.semibold))
                                .foregroundStyle(Theme.textPrimary)
                            Text("Откройте на ПК «Подключить iPhone…» в меню значка Nexus PC и вставьте ссылку. Подключение по сети появится в следующей сборке.")
                                .font(.subheadline)
                                .foregroundStyle(Theme.textMuted)
                        }
                    }
                    NeonButton(title: "Проверить ссылку сопряжения", systemImage: "link") {
                        showingPairing = true
                    }
                    Text("Версия \(appVersion) · протокол v\(NexusProtocol.version)")
                        .font(.footnote)
                        .foregroundStyle(Theme.textMuted)
                        .frame(maxWidth: .infinity)
                }
                .padding(20)
            }
        }
        .sheet(isPresented: $showingPairing) {
            PairingLinkView()
        }
    }

    private var appVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("NEXUS PC")
                .font(.system(size: 34, weight: .heavy, design: .rounded))
                .foregroundStyle(Theme.accent)
            Text("Command Center")
                .font(.headline)
                .foregroundStyle(Theme.textMuted)
        }
        .padding(.top, 12)
    }
}

struct PairingLinkView: View {
    @State private var model = PairingLinkViewModel()
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ZStack {
                Theme.background.ignoresSafeArea()
                VStack(alignment: .leading, spacing: 16) {
                    TextField("nexuspc://pair?…", text: $model.text, axis: .vertical)
                        .lineLimit(3...6)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.system(.footnote, design: .monospaced))
                        .padding(12)
                        .background(Theme.surface, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                        .foregroundStyle(Theme.textPrimary)

                    if let link = model.link {
                        GlassCard {
                            VStack(alignment: .leading, spacing: 6) {
                                Label(link.pcName, systemImage: "checkmark.seal.fill")
                                    .font(.headline)
                                    .foregroundStyle(Theme.cyan)
                                Text("Адреса: \(link.hosts.joined(separator: ", ")) · порт \(link.port)")
                                Text("Отпечаток: \(link.certificateFingerprint.prefix(16))…")
                                    .font(.system(.caption, design: .monospaced))
                            }
                            .foregroundStyle(Theme.textPrimary)
                        }
                    } else if model.hasInput {
                        Label("Это не ссылка сопряжения Nexus PC", systemImage: "xmark.octagon")
                            .foregroundStyle(.red)
                    }
                    Spacer()
                }
                .padding(20)
            }
            .navigationTitle("Ссылка сопряжения")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Закрыть") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    Button("Вставить") {
                        model.text = UIPasteboard.general.string ?? ""
                    }
                }
            }
        }
    }
}
