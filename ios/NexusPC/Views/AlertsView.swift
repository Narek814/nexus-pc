import SwiftUI
import UIKit

/// Smart Alerts settings (P4b): thresholds, ntfy delivery (off by default), test, recent alerts.
struct AlertsView: View {
    let controller: PCController

    @State private var settings = AlertSettingsModel()
    @State private var saved = AlertSettingsModel()
    @State private var ntfyEnabled = false
    @State private var topic: String?
    @State private var server = "https://ntfy.sh"
    @State private var recent: [(time: String, title: String, message: String, delivered: Bool)] = []
    @State private var status: String?
    @State private var busy = false

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if let status {
                        Text(status).font(.footnote).foregroundStyle(Theme.cyan)
                    }
                    SectionTitle("Уведомления на iPhone")
                    ntfyCard
                    SectionTitle("Когда предупреждать")
                    thresholdsCard
                    if !recent.isEmpty {
                        SectionTitle("Последние события")
                        recentCard
                    }
                }
                .padding(20)
            }
        }
        .navigationTitle("Уведомления")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
    }

    private var ntfyCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 12) {
                Toggle("Присылать через ntfy", isOn: Binding(get: { ntfyEnabled }, set: { value in Task { await setNtfy(value) } }))
                    .tint(Theme.violet)
                    .disabled(busy)
                Text("ПК отправляет короткие сообщения (имя ПК и событие, без файлов и программ) через бесплатный сервис ntfy. Их видно, даже когда Nexus PC закрыт.")
                    .font(.caption)
                    .foregroundStyle(Theme.textMuted)
                if ntfyEnabled, let topic {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("1. Установите приложение **ntfy** из App Store.")
                        Text("2. В ntfy нажмите «+», вставьте канал и нажмите Subscribe.")
                        Text("3. Разрешите ntfy присылать уведомления.")
                    }
                    .font(.footnote)
                    .foregroundStyle(Theme.textPrimary)
                    HStack {
                        Text(topic).font(.system(.caption, design: .monospaced)).foregroundStyle(Theme.cyan).lineLimit(1).truncationMode(.middle)
                        Spacer()
                        Button("Скопировать") {
                            UIPasteboard.general.string = topic
                            status = "Канал скопирован"
                        }
                        .foregroundStyle(Theme.cyan)
                    }
                    Text("Сервер: \(server). Канал — секрет: знающий его может читать уведомления.")
                        .font(.caption2)
                        .foregroundStyle(Theme.textMuted)
                    NeonButton(title: "Отправить тестовое", systemImage: "bell.badge") {
                        Task { await sendTest() }
                    }
                }
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var thresholdsCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 14) {
                rule("Видеокарта горячее", isOn: $settings.gpuTemperatureEnabled, value: $settings.gpuTemperatureC, range: 50...110, unit: "°C", note: "дольше 30 с")
                rule("Процессор загружен больше", isOn: $settings.cpuUsageEnabled, value: $settings.cpuUsagePercent, range: 50...100, unit: "%", note: "дольше 5 мин")
                rule("Память занята больше", isOn: $settings.memoryUsageEnabled, value: $settings.memoryUsagePercent, range: 50...100, unit: "%", note: "дольше 5 мин")
                rule("На диске свободно меньше", isOn: $settings.diskFreeEnabled, value: $settings.diskFreePercent, range: 1...50, unit: "%", note: nil)
                Toggle("ПК включился / выключился / выключился неожиданно", isOn: $settings.powerEventsEnabled).tint(Theme.violet)
                Stepper("Не повторять чаще, чем раз в \(settings.cooldownMinutes) мин", value: $settings.cooldownMinutes, in: 5...1440, step: 5)
                if settings != saved {
                    NeonButton(title: "Сохранить", systemImage: "checkmark") {
                        Task { await save() }
                    }
                }
            }
            .font(.subheadline)
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private func rule(_ title: String, isOn: Binding<Bool>, value: Binding<Int>, range: ClosedRange<Int>, unit: String, note: String?) -> some View {
        let suffix = note.map { " · " + $0 } ?? ""
        return VStack(alignment: .leading, spacing: 4) {
            Toggle(title, isOn: isOn).tint(Theme.violet)
            if isOn.wrappedValue {
                Stepper("\(value.wrappedValue)\(unit)\(suffix)", value: value, in: range)
                    .foregroundStyle(Theme.cyan)
            }
        }
    }

    private var recentCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 10) {
                ForEach(Array(recent.enumerated()), id: \.offset) { _, item in
                    VStack(alignment: .leading, spacing: 2) {
                        HStack {
                            Text(item.title).font(.subheadline.weight(.semibold)).foregroundStyle(Theme.textPrimary)
                            Spacer()
                            Image(systemName: item.delivered ? "checkmark.circle.fill" : "bell.slash")
                                .foregroundStyle(item.delivered ? .green : Theme.textMuted)
                        }
                        Text(item.message).font(.caption).foregroundStyle(Theme.textMuted)
                        Text(item.time).font(.caption2).foregroundStyle(Theme.textMuted)
                    }
                }
            }
        }
    }

    // MARK: - Data

    private func load() async {
        do {
            apply(try await controller.fetch("alerts.get"))
        } catch {
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    private func apply(_ p: [String: Any]) {
        settings = AlertSettingsModel(p)
        saved = settings
        let ntfy = p["ntfy"] as? [String: Any] ?? [:]
        ntfyEnabled = ntfy["enabled"] as? Bool ?? false
        topic = ntfy["topic"] as? String
        server = ntfy["server"] as? String ?? server
        recent = (p["recent"] as? [[String: Any]] ?? []).map { item in
            let date = (item["time"] as? String).flatMap(MetricsPoint.parseDate)
            let time = date.map { $0.formatted(date: .abbreviated, time: .shortened) } ?? ""
            return (time, item["title"] as? String ?? "", item["message"] as? String ?? "", item["delivered"] as? Bool ?? false)
        }
    }

    private func setNtfy(_ enabled: Bool) async {
        busy = true
        defer { busy = false }
        do {
            apply(try await controller.fetch("alerts.set", payload: ["ntfyEnabled": enabled]))
            status = enabled ? "Уведомления через ntfy включены" : "Уведомления через ntfy выключены"
        } catch {
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    private func save() async {
        do {
            apply(try await controller.fetch("alerts.set", payload: settings.payload))
            status = "Сохранено"
        } catch {
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    private func sendTest() async {
        do {
            let result = try await controller.fetch("alerts.test")
            status = (result["delivered"] as? Bool ?? false)
                ? "Отправлено. Проверьте уведомление ntfy."
                : "ПК не смог отправить: нет интернета на ПК или ntfy недоступен."
            await load()
        } catch {
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}

struct AlertSettingsModel: Equatable {
    var gpuTemperatureEnabled = true
    var gpuTemperatureC = 85
    var cpuUsageEnabled = true
    var cpuUsagePercent = 95
    var memoryUsageEnabled = true
    var memoryUsagePercent = 90
    var diskFreeEnabled = true
    var diskFreePercent = 10
    var powerEventsEnabled = true
    var cooldownMinutes = 30

    init() {}

    init(_ p: [String: Any]) {
        gpuTemperatureEnabled = p["gpuTemperatureEnabled"] as? Bool ?? true
        gpuTemperatureC = p["gpuTemperatureC"] as? Int ?? 85
        cpuUsageEnabled = p["cpuUsageEnabled"] as? Bool ?? true
        cpuUsagePercent = p["cpuUsagePercent"] as? Int ?? 95
        memoryUsageEnabled = p["memoryUsageEnabled"] as? Bool ?? true
        memoryUsagePercent = p["memoryUsagePercent"] as? Int ?? 90
        diskFreeEnabled = p["diskFreeEnabled"] as? Bool ?? true
        diskFreePercent = p["diskFreePercent"] as? Int ?? 10
        powerEventsEnabled = p["powerEventsEnabled"] as? Bool ?? true
        cooldownMinutes = p["cooldownMinutes"] as? Int ?? 30
    }

    var payload: [String: Any] {
        [
            "gpuTemperatureEnabled": gpuTemperatureEnabled, "gpuTemperatureC": gpuTemperatureC,
            "cpuUsageEnabled": cpuUsageEnabled, "cpuUsagePercent": cpuUsagePercent,
            "memoryUsageEnabled": memoryUsageEnabled, "memoryUsagePercent": memoryUsagePercent,
            "diskFreeEnabled": diskFreeEnabled, "diskFreePercent": diskFreePercent,
            "powerEventsEnabled": powerEventsEnabled, "cooldownMinutes": cooldownMinutes,
        ]
    }
}
