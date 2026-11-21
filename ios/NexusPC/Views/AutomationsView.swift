import NexusKit
import SwiftUI
import UIKit

/// Automations (P6): rules that run on the PC itself — on a schedule, when a sensor crosses a
/// threshold, when the PC starts or someone signs in to Windows.
struct AutomationsView: View {
    let controller: PCController

    @State private var rules: [AutomationDraft] = []
    @State private var status: String?
    @State private var editing: AutomationDraft?
    @State private var confirming: PendingConfirmation?

    /// A rule that shuts down or restarts the PC: the agent wants `confirmed`, so the person is asked first (T3).
    enum PendingConfirmation: Identifiable {
        case run(AutomationDraft)
        case setEnabled(AutomationDraft, Bool)

        var id: String {
            switch self {
            case let .run(rule): "run-\(rule.id)"
            case let .setEnabled(rule, value): "enable-\(rule.id)-\(value)"
            }
        }

        var title: String {
            switch self {
            case let .run(rule):
                "«\(rule.name)» выключает или перезагружает ПК. Windows покажет отсчёт, его можно отменить. Запустить сейчас?"
            case let .setEnabled(rule, value):
                value
                    ? "«\(rule.name)» выключает или перезагружает ПК. Windows покажет отсчёт, его можно отменить. Включить автоматизацию?"
                    : "Отключить автоматизацию «\(rule.name)»? Она выключает или перезагружает ПК, поэтому ПК просит подтвердить изменение."
            }
        }
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if let status {
                        Text(status).font(.footnote).foregroundStyle(.orange).textSelection(.enabled)
                    }
                    if rules.isEmpty {
                        GlassCard {
                            Text("Автоматизаций пока нет. Нажмите «+» — там есть готовые шаблоны: «Ночь», «Перегрев видеокарты», «При входе в Windows».")
                                .font(.subheadline)
                                .foregroundStyle(Theme.textMuted)
                        }
                    }
                    ForEach(rules) { rule in
                        ruleCard(rule)
                    }
                    Text("Автоматизации выполняет сам ПК, даже когда телефон выключен. Расписание — по времени ПК; если ПК в это время спит или выключен, правило пропускается.")
                        .font(.caption2)
                        .foregroundStyle(Theme.textMuted)
                }
                .padding(20)
            }
            .refreshable { await load() }
        }
        .navigationTitle("Автоматизации")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button("Пустая") { editing = AutomationDraft.blank(controller) }
                    Divider()
                    Button("Шаблон «Ночь»") { editing = AutomationDraft.night() }
                    Button("Шаблон «Перегрев видеокарты»") { editing = AutomationDraft.hotGpu() }
                    Button("Шаблон «Перегрев процессора»") { editing = AutomationDraft.hotCpu() }
                    Button("Шаблон «При входе в Windows»") { editing = AutomationDraft.signIn(controller) }
                } label: {
                    Image(systemName: "plus")
                }
                .tint(Theme.cyan)
            }
        }
        .task { await load() }
        .sheet(item: $editing) { draft in
            AutomationEditor(controller: controller, draft: draft) { await load() }
        }
        .confirmationDialog(confirming?.title ?? "", isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }),
                            titleVisibility: .visible, presenting: confirming) { pending in
            switch pending {
            case let .run(rule):
                Button("Запустить", role: .destructive) { Task { await command("automations.run", rule, confirmed: true) } }
            case let .setEnabled(rule, value):
                Button(value ? "Включить" : "Отключить", role: value ? .destructive : nil) { Task { await setEnabled(rule, value) } }
            }
            Button("Отмена", role: .cancel) {}
        }
    }

    private func ruleCard(_ rule: AutomationDraft) -> some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(rule.name).font(.headline)
                        Text(rule.triggerSummary).font(.caption).foregroundStyle(Theme.cyan)
                    }
                    Spacer()
                    // Until the person agrees, the switch keeps showing the saved state (it reads `rules`).
                    Toggle("", isOn: Binding(get: { rule.enabled }, set: { value in
                        if rule.needsConfirmation {
                            confirming = .setEnabled(rule, value)
                        } else {
                            Task { await setEnabled(rule, value) }
                        }
                    }))
                        .labelsHidden()
                        .tint(Theme.violet)
                }
                Text(rule.actionsSummary)
                    .font(.caption)
                    .foregroundStyle(Theme.textMuted)
                if let lastRun = rule.lastRun {
                    Text(lastRun)
                        .font(.caption2)
                        .foregroundStyle(rule.lastRunOk ? Theme.textMuted : .orange)
                }
                HStack(spacing: 16) {
                    if rule.running {
                        Button("Остановить") { Task { await command("automations.stop", rule) } }
                            .foregroundStyle(.orange)
                    } else {
                        Button("Запустить сейчас") {
                            if rule.needsConfirmation {
                                confirming = .run(rule)
                            } else {
                                Task { await command("automations.run", rule) }
                            }
                        }
                            .foregroundStyle(Theme.cyan)
                    }
                    Button("Изменить") { editing = rule }
                        .foregroundStyle(Theme.cyan)
                    Spacer()
                    Button(role: .destructive) {
                        Task { await command("automations.delete", rule) }
                    } label: {
                        Image(systemName: "trash")
                    }
                    .foregroundStyle(.red)
                }
                .font(.footnote.weight(.semibold))
                .buttonStyle(.plain)
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private func load() async {
        do {
            apply(try await controller.fetch("automations.list"))
        } catch {
            status = AutomationDraft.describe(error, controller: controller)
        }
    }

    private func apply(_ result: [String: Any]) {
        rules = (result["automations"] as? [[String: Any]] ?? []).compactMap(AutomationDraft.init(json:))
    }

    private func setEnabled(_ rule: AutomationDraft, _ value: Bool) async {
        var changed = rule
        changed.enabled = value
        do {
            // Only reached for a shutdown/restart rule after the person agreed in the dialog.
            apply(try await controller.fetch("automations.upsert", payload: ["automation": changed.payload], confirmed: changed.needsConfirmation))
            status = nil
        } catch {
            await show(error)
        }
    }

    private func command(_ type: String, _ rule: AutomationDraft, confirmed: Bool = false) async {
        do {
            let result = try await controller.fetch(type, payload: ["automationId": rule.id], confirmed: confirmed)
            if type == "automations.delete" {
                apply(result)
            } else {
                try? await Task.sleep(nanoseconds: 300_000_000)
                await load()
            }
            status = type == "automations.run" ? "«\(rule.name)» запущена" : nil
        } catch {
            await show(error)
        }
    }

    /// A rule the PC does not know (deleted, or not this iPhone's): the list is reloaded so it disappears.
    private func show(_ error: Error) async {
        if (error as? NexusError)?.code == "automations.notFound" {
            await load()
        }
        status = AutomationDraft.describe(error, controller: controller)
    }
}

struct AutomationEditor: View {
    let controller: PCController
    @State var draft: AutomationDraft
    let onSaved: () async -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var rgbProfiles: [RgbChoice] = []
    @State private var error: String?
    @State private var confirming = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Название", text: $draft.name)
                    Toggle("Включена", isOn: $draft.enabled)
                }
                triggerSection
                Section {
                    Toggle("Только в определённое время", isOn: $draft.useWindow)
                    if draft.useWindow {
                        DatePicker("С", selection: $draft.windowFrom, displayedComponents: .hourAndMinute)
                        DatePicker("До", selection: $draft.windowTo, displayedComponents: .hourAndMinute)
                    }
                } footer: {
                    Text("Например, перегрев ночью: с 22:00 до 07:00.")
                }
                actionsSection
                if let error {
                    Section { Text(error).foregroundStyle(.orange).font(.footnote) }
                }
            }
            .navigationTitle(draft.isNew ? "Новая автоматизация" : "Автоматизация")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Отмена") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Сохранить") {
                        if draft.needsConfirmation {
                            confirming = true
                        } else {
                            Task { await save() }
                        }
                    }
                    .disabled(draft.name.trimmingCharacters(in: .whitespaces).isEmpty || draft.actions.allSatisfy { $0.kind == .wait })
                }
            }
            .confirmationDialog("Эта автоматизация выключает или перезагружает ПК. Windows покажет отсчёт, его можно отменить. Сохранить?", isPresented: $confirming, titleVisibility: .visible) {
                Button("Сохранить") { Task { await save() } }
            }
            .task { await loadRgbProfiles() }
        }
    }

    private var triggerSection: some View {
        Section("Когда") {
            Picker("Событие", selection: $draft.trigger) {
                ForEach(AutomationDraft.TriggerType.allCases) { Text($0.title).tag($0) }
            }
            switch draft.trigger {
            case .time:
                DatePicker("Время", selection: $draft.time, displayedComponents: .hourAndMinute)
                HStack(spacing: 6) {
                    ForEach(1...7, id: \.self) { day in
                        let on = draft.days.contains(day)
                        Button(AutomationDraft.dayNames[day - 1]) {
                            if on, draft.days.count > 1 { draft.days.remove(day) } else { draft.days.insert(day) }
                        }
                        .font(.caption.weight(.semibold))
                        .frame(maxWidth: .infinity, minHeight: 32)
                        .background(on ? Theme.violet.opacity(0.35) : Color.clear, in: Capsule())
                        .overlay(Capsule().strokeBorder(Theme.violet.opacity(0.5)))
                        .buttonStyle(.plain)
                    }
                }
            case .metric:
                Picker("Датчик", selection: $draft.metric) {
                    Text("Температура видеокарты").tag("gpuTemperature")
                    Text("Температура процессора").tag("cpuTemperature")
                    Text("Загрузка процессора").tag("cpuUsage")
                    Text("Занятая память").tag("memoryUsage")
                }
                Stepper("Выше \(draft.above)\(draft.metric.hasSuffix("Temperature") ? " °C" : " %")", value: $draft.above, in: 1...120)
                Stepper("Дольше \(draft.forSeconds) с", value: $draft.forSeconds, in: 0...3600, step: 10)
            case .pcStarted:
                Text("Через 15 секунд после запуска службы Nexus PC (до входа в Windows программы не запускаются — для них выберите «Вход в Windows»).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            case .userSignedIn:
                Text("Когда вы входите в Windows (не чаще раза в 10 минут).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var actionsSection: some View {
        Section {
            ForEach($draft.actions) { $action in
                actionRow($action)
            }
            .onDelete { draft.actions.remove(atOffsets: $0) }
            .onMove { draft.actions.move(fromOffsets: $0, toOffset: $1) }
            if draft.actions.count < 8 {
                Menu("Добавить действие") {
                    ForEach(AutomationDraft.ActionKind.allCases) { kind in
                        Button(kind.title) { draft.actions.append(.init(kind: kind, controller: controller, rgbProfiles: rgbProfiles)) }
                    }
                }
            }
        } header: {
            Text("Что сделать (по порядку)")
        } footer: {
            Text("Смахните влево, чтобы удалить шаг. Программы и профили — из списка ПК; для них нужен вход в Windows.")
        }
    }

    @ViewBuilder
    private func actionRow(_ action: Binding<AutomationDraft.Action>) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(action.wrappedValue.kind.title).font(.subheadline.weight(.semibold))
            switch action.wrappedValue.kind {
            case .profile:
                Picker("Профиль", selection: action.target) {
                    ForEach(controller.profiles) { Text($0.name).tag($0.id) }
                }
            case .app:
                Picker("Программа", selection: action.target) {
                    ForEach(controller.apps) { Text($0.name).tag($0.id) }
                }
            case .rgbProfile:
                Picker("Профиль подсветки", selection: action.target) {
                    ForEach(rgbProfiles) { Text($0.name).tag($0.id) }
                }
            case .rgb:
                ColorPicker("Цвет", selection: action.color, supportsOpacity: false)
                Stepper("Яркость \(action.wrappedValue.brightness)%", value: action.brightness, in: 0...100, step: 10)
            case .rgbOff, .lock, .sleep:
                EmptyView()
            case .shutdown, .restart:
                Stepper("Через \(action.wrappedValue.minutes) мин (с отсчётом Windows)", value: action.minutes, in: 1...1440)
            case .wait:
                Stepper("Подождать \(action.wrappedValue.minutes) мин", value: action.minutes, in: 1...240)
            }
        }
    }

    private func loadRgbProfiles() async {
        guard controller.can("rgb.status"), let result = try? await controller.fetch("rgb.status") else { return }
        rgbProfiles = (result["profiles"] as? [[String: Any]] ?? []).compactMap { p in
            guard let id = p["id"] as? String, let name = p["name"] as? String else { return nil }
            return RgbChoice(id: id, name: name)
        }
    }

    private func save() async {
        do {
            _ = try await controller.fetch("automations.upsert", payload: ["automation": draft.payload], confirmed: draft.needsConfirmation)
            await onSaved()
            dismiss()
        } catch {
            if (error as? NexusError)?.code == "automations.notFound" {
                await onSaved() // reloads the list behind the editor
            }
            self.error = AutomationDraft.describe(error, controller: controller)
        }
    }
}

struct RgbChoice: Identifiable {
    let id: String
    let name: String
}

/// Editable form of an automation, convertible to and from the wire format (docs/API.md).
struct AutomationDraft: Identifiable {
    enum TriggerType: String, CaseIterable, Identifiable {
        case time, metric, pcStarted, userSignedIn
        var id: String { rawValue }
        var title: String {
            switch self {
            case .time: "По расписанию"
            case .metric: "По датчику"
            case .pcStarted: "ПК включился"
            case .userSignedIn: "Вход в Windows"
            }
        }
    }

    enum ActionKind: String, CaseIterable, Identifiable {
        case profile, app, rgb, rgbProfile, rgbOff, lock, sleep, shutdown, restart, wait
        var id: String { rawValue }
        var title: String {
            switch self {
            case .profile: "Запустить профиль"
            case .app: "Запустить программу"
            case .rgb: "Подсветка: цвет"
            case .rgbProfile: "Подсветка: профиль"
            case .rgbOff: "Выключить подсветку"
            case .lock: "Заблокировать ПК"
            case .sleep: "Сон"
            case .shutdown: "Выключить ПК"
            case .restart: "Перезагрузить ПК"
            case .wait: "Пауза"
            }
        }
    }

    struct Action: Identifiable {
        let id = UUID()
        var kind: ActionKind
        var target = ""
        var color = Color(red: 0.6, green: 0.2, blue: 1)
        var brightness = 100
        var minutes = 10

        init(kind: ActionKind) {
            self.kind = kind
        }

        @MainActor
        init(kind: ActionKind, controller: PCController, rgbProfiles: [RgbChoice]) {
            self.kind = kind
            switch kind {
            case .profile: target = controller.profiles.first?.id ?? ""
            case .app: target = controller.apps.first?.id ?? ""
            case .rgbProfile: target = rgbProfiles.first?.id ?? ""
            case .wait: minutes = 30
            case .shutdown, .restart: minutes = 5
            default: break
            }
        }

        var payload: [String: Any] {
            switch kind {
            case .profile: ["type": "profile", "profileId": target]
            case .app: ["type": "app", "appId": target]
            case .rgb: ["type": "rgb", "color": color.hexString, "brightness": brightness, "effect": "static"]
            case .rgbProfile: ["type": "rgbProfile", "profileId": target]
            case .rgbOff: ["type": "rgb", "color": "#000000", "brightness": 0, "effect": "off"]
            case .lock: ["type": "lock"]
            case .sleep: ["type": "sleep"]
            case .shutdown: ["type": "shutdown", "delaySeconds": minutes * 60]
            case .restart: ["type": "restart", "delaySeconds": minutes * 60]
            case .wait: ["type": "wait", "seconds": minutes * 60]
            }
        }

        init?(json p: [String: Any]) {
            guard let type = p["type"] as? String else { return nil }
            switch type {
            case "profile": kind = .profile; target = p["profileId"] as? String ?? ""
            case "app": kind = .app; target = p["appId"] as? String ?? ""
            case "rgbProfile": kind = .rgbProfile; target = p["profileId"] as? String ?? ""
            case "rgb":
                if (p["effect"] as? String)?.lowercased() == "off" {
                    kind = .rgbOff
                } else {
                    kind = .rgb
                    color = Color(hex: p["color"] as? String ?? "") ?? color
                    brightness = p["brightness"] as? Int ?? 100
                }
            case "lock": kind = .lock
            case "sleep": kind = .sleep
            case "shutdown": kind = .shutdown; minutes = max(1, (p["delaySeconds"] as? Int ?? 300) / 60)
            case "restart": kind = .restart; minutes = max(1, (p["delaySeconds"] as? Int ?? 300) / 60)
            case "wait": kind = .wait; minutes = max(1, (p["seconds"] as? Int ?? 60) / 60)
            default: return nil
            }
        }

        var summary: String {
            switch kind {
            case .profile: "профиль «\(target)»"
            case .app: "программа «\(target)»"
            case .rgb: "подсветка \(color.hexString)"
            case .rgbProfile: "подсветка «\(target)»"
            case .rgbOff: "подсветка выкл."
            case .lock: "блокировка"
            case .sleep: "сон"
            case .shutdown: "выключение через \(minutes) мин"
            case .restart: "перезагрузка через \(minutes) мин"
            case .wait: "пауза \(minutes) мин"
            }
        }
    }

    static let dayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"]

    var id: String
    var isNew: Bool
    var name: String
    var enabled = true
    var trigger: TriggerType = .time
    var time = AutomationDraft.date(23, 0)
    var days: Set<Int> = Set(1...7)
    var metric = "gpuTemperature"
    var above = 85
    var forSeconds = 30
    var useWindow = false
    var windowFrom = AutomationDraft.date(22, 0)
    var windowTo = AutomationDraft.date(7, 0)
    var actions: [Action] = []
    var running = false
    var lastRun: String?
    var lastRunOk = true

    init(name: String) {
        self.name = name
        id = "auto-" + String(UUID().uuidString.prefix(8)).lowercased()
        isNew = true
    }

    init?(json p: [String: Any]) {
        guard let id = p["id"] as? String, let name = p["name"] as? String, let t = p["trigger"] as? [String: Any],
              let type = t["type"] as? String, let trigger = TriggerType(rawValue: type) else { return nil }
        self.id = id
        self.name = name
        isNew = false
        enabled = p["enabled"] as? Bool ?? true
        self.trigger = trigger
        if let text = t["time"] as? String, let date = Self.parse(text) { time = date }
        if let list = t["days"] as? [Int] { days = Set(list) }
        metric = t["metric"] as? String ?? metric
        above = t["above"] as? Int ?? above
        forSeconds = t["forSeconds"] as? Int ?? forSeconds
        if let window = p["onlyBetween"] as? [String: Any], let from = (window["from"] as? String).flatMap(Self.parse), let to = (window["to"] as? String).flatMap(Self.parse) {
            useWindow = true
            windowFrom = from
            windowTo = to
        }
        actions = (p["actions"] as? [[String: Any]] ?? []).compactMap(Action.init(json:))
        running = p["running"] as? Bool ?? false
        if let last = p["lastRun"] as? [String: Any] {
            lastRunOk = last["ok"] as? Bool ?? true
            let when = (last["ts"] as? String).flatMap { ISO8601DateFormatter().date(from: $0) }?.formatted(date: .abbreviated, time: .shortened) ?? ""
            lastRun = "Последний запуск \(when): \(last["summary"] as? String ?? "")"
        }
    }

    var needsConfirmation: Bool { actions.contains { $0.kind == .shutdown || $0.kind == .restart } }

    var payload: [String: Any] {
        var t: [String: Any] = ["type": trigger.rawValue]
        switch trigger {
        case .time:
            t["time"] = Self.format(time)
            t["days"] = days.sorted()
        case .metric:
            t["metric"] = metric
            t["above"] = above
            t["forSeconds"] = forSeconds
        case .pcStarted, .userSignedIn:
            break
        }
        var json: [String: Any] = [
            "id": id, "name": String(name.trimmingCharacters(in: .whitespaces).prefix(64)), "enabled": enabled,
            "trigger": t, "actions": actions.map(\.payload),
        ]
        if useWindow, Self.format(windowFrom) != Self.format(windowTo) {
            json["onlyBetween"] = ["from": Self.format(windowFrom), "to": Self.format(windowTo)]
        }
        return json
    }

    var triggerSummary: String {
        var text: String
        switch trigger {
        case .time:
            let dayText = days.count == 7 ? "каждый день" : days.sorted().map { Self.dayNames[$0 - 1] }.joined(separator: ", ")
            text = "В \(Self.format(time)), \(dayText)"
        case .metric:
            let names = ["gpuTemperature": "Видеокарта", "cpuTemperature": "Процессор", "cpuUsage": "Загрузка процессора", "memoryUsage": "Память"]
            text = "\(names[metric] ?? metric) выше \(above)\(metric.hasSuffix("Temperature") ? " °C" : " %")" + (forSeconds > 0 ? " дольше \(forSeconds) с" : "")
        case .pcStarted:
            text = "Когда ПК включился"
        case .userSignedIn:
            text = "При входе в Windows"
        }
        if useWindow {
            text += " · только \(Self.format(windowFrom))–\(Self.format(windowTo))"
        }
        return text
    }

    var actionsSummary: String { actions.map(\.summary).joined(separator: " → ") }

    // MARK: - Templates

    @MainActor
    static func blank(_ controller: PCController) -> AutomationDraft {
        var draft = AutomationDraft(name: "")
        draft.actions = [Action(kind: .lock)]
        return draft
    }

    static func night() -> AutomationDraft {
        var draft = AutomationDraft(name: "Ночь")
        draft.trigger = .time
        draft.time = date(23, 30)
        var wait = Action(kind: .wait)
        wait.minutes = 30
        draft.actions = [Action(kind: .rgbOff), wait, Action(kind: .sleep)]
        return draft
    }

    static func hotGpu() -> AutomationDraft {
        var draft = AutomationDraft(name: "Перегрев видеокарты")
        draft.trigger = .metric
        var red = Action(kind: .rgb)
        red.color = .red
        draft.actions = [red]
        return draft
    }

    static func hotCpu() -> AutomationDraft {
        var draft = AutomationDraft(name: "Перегрев процессора")
        draft.trigger = .metric
        draft.metric = "cpuTemperature"
        draft.above = 90
        draft.forSeconds = 60
        var red = Action(kind: .rgb)
        red.color = .red
        draft.actions = [red]
        return draft
    }

    @MainActor
    static func signIn(_ controller: PCController) -> AutomationDraft {
        var draft = AutomationDraft(name: "При входе в Windows")
        draft.trigger = .userSignedIn
        draft.actions = [controller.profiles.isEmpty
            ? Action(kind: .rgb)
            : Action(kind: .profile, controller: controller, rgbProfiles: [])]
        return draft
    }

    // MARK: - Helpers

    static func date(_ hour: Int, _ minute: Int) -> Date {
        Calendar.current.date(bySettingHour: hour, minute: minute, second: 0, of: Date()) ?? Date()
    }

    static func format(_ date: Date) -> String {
        let c = Calendar.current.dateComponents([.hour, .minute], from: date)
        return String(format: "%02d:%02d", c.hour ?? 0, c.minute ?? 0)
    }

    static func parse(_ text: String) -> Date? {
        let parts = text.split(separator: ":").compactMap { Int($0) }
        guard parts.count == 2 else { return nil }
        return date(parts[0], parts[1])
    }

    @MainActor
    static func describe(_ error: Error, controller: PCController) -> String {
        let nexus = error as? NexusError
        if nexus?.code == "automations.notFound" {
            // Deleted, or not created by this iPhone (the PC does not tell which). Not a permission problem.
            return "Такой автоматизации нет. Список обновлён."
        }
        if nexus?.code == "auth.permissionDenied", case let .server(_, message)? = nexus, message.contains("разрешения:") {
            return message + " Добавьте их этому iPhone командой администратора на ПК."
        }
        // The command-level check ("Device lacks permission for 'automations.…'"): this iPhone has no
        // "automation" permission. Any other refusal shows the PC's own text instead of the grant-all advice.
        if nexus?.code == "auth.permissionDenied", case let .server(_, message)? = nexus, !message.contains("automations.") {
            return message
        }
        if nexus?.code == "auth.permissionDenied" {
            let id = controller.pc?.deviceId.uuidString.lowercased() ?? "<deviceId>"
            return "Автоматизации не разрешены для этого iPhone. На ПК в PowerShell от имени администратора:\n& \"C:\\Program Files\\NexusPC\\Agent\\Nexus.Agent.exe\" devices permissions \(id) power,apps,monitoring,rgb,input,files,clipboard,automation"
        }
        if case let .server(_, message)? = nexus {
            return message
        }
        return (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }
}

/// PC history (P6): what happened on the PC, when and from which device.
struct HistoryView: View {
    let controller: PCController

    @State private var entries: [Entry] = []
    @State private var status: String?
    @State private var more = true

    struct Entry: Identifiable {
        let id = UUID()
        let date: Date
        let rawTs: String
        let kind: String
        let title: String
        let ok: Bool
        let detail: String?
        let device: String?

        var symbol: String {
            switch kind {
            case "automation": "gearshape.2.fill"
            case "alert": "bell.fill"
            default: "iphone"
            }
        }
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            List {
                if let status {
                    Text(status).font(.footnote).foregroundStyle(.orange)
                }
                ForEach(groups) { group in
                    Section(group.title) {
                        ForEach(group.items) { entry in
                            row(entry)
                        }
                    }
                }
                if more, !entries.isEmpty {
                    Button("Показать ещё") { Task { await load(more: true) } }
                        .foregroundStyle(Theme.cyan)
                }
            }
            .scrollContentBackground(.hidden)
            .refreshable { await load(more: false) }
        }
        .navigationTitle("История")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load(more: false) }
    }

    struct DayGroup: Identifiable {
        let id: Date
        let title: String
        let items: [Entry]
    }

    private var groups: [DayGroup] {
        let grouped = Dictionary(grouping: entries) { Calendar.current.startOfDay(for: $0.date) }
        return grouped.keys.sorted(by: >).map { day in
            DayGroup(id: day, title: day.formatted(date: .complete, time: .omitted), items: grouped[day]!.sorted { $0.date > $1.date })
        }
    }

    private func row(_ entry: Entry) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: entry.symbol)
                .foregroundStyle(entry.ok ? Theme.violet : .orange)
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.title).font(.subheadline)
                if let detail = entry.detail {
                    Text(detail).font(.caption).foregroundStyle(.secondary)
                }
                Text([entry.date.formatted(date: .omitted, time: .shortened), entry.device].compactMap { $0 }.joined(separator: " · "))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func load(more loadMore: Bool) async {
        var payload: [String: Any] = ["limit": 100]
        if loadMore, let last = entries.last {
            payload["before"] = last.rawTs
        }
        do {
            let result = try await controller.fetch("history.list", payload: payload)
            let iso = ISO8601DateFormatter()
            let page: [Entry] = (result["entries"] as? [[String: Any]] ?? []).compactMap { e in
                guard let ts = e["ts"] as? String, let date = iso.date(from: ts), let title = e["title"] as? String else { return nil }
                return Entry(date: date, rawTs: ts, kind: e["kind"] as? String ?? "", title: title, ok: e["ok"] as? Bool ?? true,
                             detail: e["detail"] as? String, device: e["device"] as? String)
            }
            entries = loadMore ? entries + page : page
            more = page.count == 100
            status = entries.isEmpty ? "Пока пусто: здесь появятся команды с телефона, автоматизации и предупреждения." : nil
        } catch {
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}
