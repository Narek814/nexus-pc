import SwiftUI
import UIKit

/// Lighting (P10): colour, brightness and effect through OpenRGB on the PC, plus lighting profiles.
/// A lighting profile with the same id as an app profile switches together with it on the PC.
struct RgbView: View {
    let controller: PCController

    @State private var available = false
    @State private var reason: String?
    @State private var devices: [RgbDeviceInfo] = []
    @State private var profiles: [RgbProfileInfo] = []
    @State private var color = Color(red: 0.6, green: 0.2, blue: 1.0)
    @State private var brightness = 100.0
    @State private var effect = "static"
    @State private var status: String?
    @State private var busy = false
    @State private var saving = false
    @State private var newName = ""
    @State private var linkTo = ""

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if let status {
                        Text(status).font(.footnote).foregroundStyle(Theme.cyan)
                    }
                    if available {
                        SectionTitle("Цвет и эффект")
                        lookCard
                        SectionTitle("Профили подсветки")
                        profilesCard
                        SectionTitle("Устройства")
                        devicesCard
                    } else {
                        unavailableCard
                    }
                }
                .padding(20)
            }
            .refreshable { await load() }
        }
        .navigationTitle("Подсветка")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .sheet(isPresented: $saving) { saveSheet }
    }

    // MARK: - Cards

    private var unavailableCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 10) {
                Label("Подсветка недоступна", systemImage: "lightbulb.slash")
                    .font(.headline)
                Text(reason ?? "Загрузка…")
                    .font(.subheadline)
                    .foregroundStyle(Theme.textMuted)
                Text("OpenRGB входит в Nexus PC и запускается вместе с ПК. Если подсветка не отвечает: закройте Armoury Crate (он перехватывает подсветку) и перезагрузите ПК. Устройства настраиваются на ПК: значок «N» → «Настроить подсветку (OpenRGB)…».")
                    .font(.caption)
                    .foregroundStyle(Theme.textMuted)
                NeonButton(title: "Проверить снова", systemImage: "arrow.clockwise") {
                    Task { await load() }
                }
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var lookCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 14) {
                ColorPicker("Цвет", selection: $color, supportsOpacity: false)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Яркость: \(Int(brightness))%")
                    Slider(value: $brightness, in: 0...100, step: 5).tint(Theme.violet)
                }
                Picker("Эффект", selection: $effect) {
                    Text("Ровный цвет").tag("static")
                    Text("Выключить").tag("off")
                    ForEach(deviceEffects, id: \.self) { name in
                        Text(name).tag(name)
                    }
                }
                .pickerStyle(.menu)
                .tint(Theme.cyan)
                Text("Яркость применяется к ровному цвету. Эффекты устройств (Breathing, Rainbow…) работают только на тех устройствах, где они есть.")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
                HStack {
                    NeonButton(title: "Применить", systemImage: "wand.and.stars") {
                        Task { await apply() }
                    }
                    .disabled(busy)
                    if controller.can("rgb.profiles.upsert") {
                        Button("Сохранить как профиль") {
                            newName = ""
                            linkTo = ""
                            saving = true
                        }
                        .foregroundStyle(Theme.cyan)
                    }
                }
            }
            .font(.subheadline)
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var profilesCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 12) {
                if profiles.isEmpty {
                    Text("Профилей пока нет. Настройте цвет и нажмите «Сохранить как профиль».")
                        .font(.caption)
                        .foregroundStyle(Theme.textMuted)
                }
                ForEach(profiles) { profile in
                    HStack(spacing: 12) {
                        Circle()
                            .fill(Color(hex: profile.color) ?? .gray)
                            .frame(width: 22, height: 22)
                            .overlay(Circle().stroke(Theme.textMuted.opacity(0.4)))
                        VStack(alignment: .leading, spacing: 2) {
                            Text(profile.name).font(.subheadline.weight(.semibold))
                            Text(profileCaption(profile))
                                .font(.caption2)
                                .foregroundStyle(Theme.textMuted)
                        }
                        Spacer()
                        Button {
                            Task { await applyProfile(profile) }
                        } label: {
                            Image(systemName: "play.fill")
                        }
                        .foregroundStyle(Theme.cyan)
                        .disabled(busy)
                    }
                    .contextMenu {
                        Button("Загрузить в настройки") { load(profile) }
                        if controller.can("rgb.profiles.delete") {
                            Button("Удалить", role: .destructive) {
                                Task { await delete(profile) }
                            }
                        }
                    }
                }
                Text("Удерживайте профиль, чтобы изменить или удалить. Профиль, связанный с профилем программ, включается вместе с ним.")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var devicesCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 8) {
                if devices.isEmpty {
                    Text("OpenRGB работает, но не нашёл ни одного устройства.")
                        .font(.caption)
                        .foregroundStyle(Theme.textMuted)
                }
                ForEach(devices) { device in
                    HStack {
                        Image(systemName: device.symbol).foregroundStyle(Theme.violet).frame(width: 24)
                        Text(device.name).font(.subheadline)
                        Spacer()
                        Text("\(device.leds) LED").font(.caption).foregroundStyle(Theme.textMuted)
                    }
                }
                Text("Подсветку монитора Skydimo OpenRGB не поддерживает — она остаётся в приложении Skydimo.")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var saveSheet: some View {
        NavigationStack {
            Form {
                if !controller.profiles.isEmpty {
                    Section {
                        Picker("Профиль программ", selection: $linkTo) {
                            Text("Не связывать").tag("")
                            ForEach(controller.profiles) { profile in
                                Text(profile.name).tag(profile.id)
                            }
                        }
                    } footer: {
                        Text("Связанный профиль подсветки включится, когда вы запустите этот профиль программ (с iPhone, через Siri или виджет).")
                    }
                }
                if linkTo.isEmpty {
                    Section("Название") {
                        TextField("Например, Вечер", text: $newName)
                    }
                }
            }
            .navigationTitle("Профиль подсветки")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Отмена") { saving = false }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Сохранить") {
                        saving = false
                        Task { await saveProfile() }
                    }
                    .disabled(linkTo.isEmpty && newName.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .presentationDetents([.medium])
    }

    // MARK: - Derived

    /// Device modes beyond the ones the agent handles itself (static colour, off, direct control).
    private var deviceEffects: [String] {
        var seen = Set<String>()
        var result: [String] = []
        for device in devices {
            for mode in device.modes where !["direct", "static", "off"].contains(mode.lowercased()) {
                if seen.insert(mode.lowercased()).inserted {
                    result.append(mode)
                }
            }
        }
        return result
    }

    private var look: [String: Any] {
        ["color": color.hexString, "brightness": Int(brightness), "effect": effect]
    }

    private func profileCaption(_ profile: RgbProfileInfo) -> String {
        let effectName = switch profile.effect.lowercased() {
        case "static": "ровный цвет"
        case "off": "выключено"
        default: profile.effect
        }
        var parts = ["\(effectName) · \(profile.brightness)%"]
        if let linked = controller.profiles.first(where: { $0.id == profile.id }) {
            parts.append("вместе с «\(linked.name)»")
        }
        return parts.joined(separator: " · ")
    }

    // MARK: - Actions

    private func load() async {
        do {
            let result = try await controller.fetch("rgb.status")
            available = result["available"] as? Bool ?? false
            reason = result["reason"] as? String
            devices = (result["devices"] as? [[String: Any]] ?? []).compactMap(RgbDeviceInfo.init)
            profiles = (result["profiles"] as? [[String: Any]] ?? []).compactMap(RgbProfileInfo.init)
            if !deviceEffects.contains(effect), !["static", "off"].contains(effect) {
                effect = "static"
            }
        } catch {
            available = false
            reason = message(error)
        }
    }

    private func apply() async {
        busy = true
        defer { busy = false }
        do {
            status = summary(try await controller.fetch("rgb.apply", payload: look))
        } catch {
            status = message(error)
        }
    }

    private func applyProfile(_ profile: RgbProfileInfo) async {
        busy = true
        defer { busy = false }
        do {
            status = "\(profile.name): " + summary(try await controller.fetch("rgb.profiles.apply", payload: ["profileId": profile.id]))
        } catch {
            status = message(error)
        }
    }

    private func load(_ profile: RgbProfileInfo) {
        color = Color(hex: profile.color) ?? color
        brightness = Double(profile.brightness)
        effect = profile.effect
    }

    private func saveProfile() async {
        let id: String
        let name: String
        if let linked = controller.profiles.first(where: { $0.id == linkTo }) {
            id = linked.id
            name = linked.name
        } else {
            name = newName.trimmingCharacters(in: .whitespaces)
            id = Slug.make(name, taken: profiles.map(\.id) + controller.profiles.map(\.id))
        }
        var profile = look
        profile["id"] = id
        profile["name"] = name
        do {
            _ = try await controller.fetch("rgb.profiles.upsert", payload: ["profile": profile])
            status = "Профиль «\(name)» сохранён"
            await load()
        } catch {
            status = message(error)
        }
    }

    private func delete(_ profile: RgbProfileInfo) async {
        do {
            _ = try await controller.fetch("rgb.profiles.delete", payload: ["profileId": profile.id])
            status = "Профиль «\(profile.name)» удалён"
            await load()
        } catch {
            status = message(error)
        }
    }

    private func summary(_ result: [String: Any]) -> String {
        let items = result["results"] as? [[String: Any]] ?? []
        let applied = result["applied"] as? Int ?? 0
        let failed = items.filter { !($0["ok"] as? Bool ?? false) }
        if failed.isEmpty {
            return "Применено на \(applied) из \(items.count) устройств"
        }
        let names = failed.compactMap { $0["name"] as? String }.joined(separator: ", ")
        let why = failed.first?["error"] as? String ?? ""
        return "Применено на \(applied) из \(items.count). Не удалось: \(names). \(why)"
    }

    private func message(_ error: Error) -> String {
        (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }
}

struct RgbDeviceInfo: Identifiable {
    let id: Int
    let name: String
    let type: String
    let leds: Int
    let modes: [String]

    init?(_ p: [String: Any]) {
        guard let index = p["index"] as? Int, let name = p["name"] as? String else { return nil }
        id = index
        self.name = name
        type = p["type"] as? String ?? ""
        leds = p["leds"] as? Int ?? 0
        modes = p["modes"] as? [String] ?? []
    }

    var symbol: String {
        switch type {
        case "mouse": "computermouse.fill"
        case "keyboard": "keyboard.fill"
        case "gpu": "cpu.fill"
        case "cooler": "fanblades.fill"
        case "ledstrip": "light.strip.2.fill"
        case "dram": "memorychip.fill"
        case "motherboard": "rectangle.3.group.fill"
        default: "lightbulb.fill"
        }
    }
}

struct RgbProfileInfo: Identifiable {
    let id: String
    let name: String
    let color: String
    let brightness: Int
    let effect: String

    init?(_ p: [String: Any]) {
        guard let id = p["id"] as? String, let name = p["name"] as? String else { return nil }
        self.id = id
        self.name = name
        color = p["color"] as? String ?? "#FFFFFF"
        brightness = p["brightness"] as? Int ?? 100
        effect = p["effect"] as? String ?? "static"
    }
}

/// Same rules as Identifiers.Slugify on the PC: ASCII letters/digits, Cyrillic transliterated, '-' between words.
enum Slug {
    static func make(_ name: String, taken: [String]) -> String {
        var out = ""
        for c in name.lowercased() {
            if c.isASCII, c.isLetter || c.isNumber {
                out.append(c)
            } else if let latin = cyrillic[c] {
                out += latin
            } else if !out.isEmpty, out.last != "-" {
                out.append("-")
            }
        }
        var slug = out.trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        if slug.isEmpty {
            slug = "light"
        }
        if slug.count > 56 {
            slug = String(slug.prefix(56)).trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        }
        var candidate = slug
        var i = 2
        while taken.contains(candidate) {
            candidate = "\(slug)-\(i)"
            i += 1
        }
        return candidate
    }

    private static let cyrillic: [Character: String] = [
        "а": "a", "б": "b", "в": "v", "г": "g", "д": "d", "е": "e", "ё": "e", "ж": "zh", "з": "z", "и": "i",
        "й": "y", "к": "k", "л": "l", "м": "m", "н": "n", "о": "o", "п": "p", "р": "r", "с": "s", "т": "t",
        "у": "u", "ф": "f", "х": "h", "ц": "ts", "ч": "ch", "ш": "sh", "щ": "sch", "ъ": "", "ы": "y", "ь": "",
        "э": "e", "ю": "yu", "я": "ya",
    ]
}

extension Color {
    init?(hex: String) {
        guard hex.count == 7, hex.first == "#", let value = UInt32(hex.dropFirst(), radix: 16) else { return nil }
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }

    var hexString: String {
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        UIColor(self).getRed(&r, green: &g, blue: &b, alpha: &a)
        func byte(_ v: CGFloat) -> Int { Int((min(max(v, 0), 1) * 255).rounded()) }
        return String(format: "#%02X%02X%02X", byte(r), byte(g), byte(b))
    }
}
