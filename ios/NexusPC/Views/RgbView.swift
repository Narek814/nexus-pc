import SwiftUI
import UIKit
import NexusKit

/// Lighting (P10): colour, brightness and effect through OpenRGB on the PC, plus lighting profiles.
/// A lighting profile with the same id as an app profile switches together with it on the PC.
/// Free (agent with plans, no `rgb.sync`): the device list from `rgb.status` is shown, the controls
/// are visible but disabled under `ProCard`.
struct RgbView: View {
    let controller: PCController

    @State private var loaded = false
    @State private var rgb: RgbStatus?
    @State private var loadError: String?
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

    init(controller: PCController) {
        self.controller = controller
    }

    private var syncLock: ProLock? {
        ProLock.check(controller.hello, feature: ProFeature.rgbSync)
    }

    /// Saving or deleting lighting profiles is not allowed for this iPhone (the section itself is).
    private var missesProfilePermission: Bool {
        !controller.can("rgb.profiles.upsert") || !controller.can("rgb.profiles.delete")
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if let status {
                        Text(verbatim: status).font(.footnote).foregroundStyle(Theme.cyan)
                    }
                    content
                }
                .padding(20)
            }
            .refreshable { await load() }
        }
        .navigationTitle(Text("section.rgb"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .sheet(isPresented: $saving) { saveSheet }
    }

    @ViewBuilder
    private var content: some View {
        if let loadError {
            ErrorCard(message: loadError) { Task { await load() } }
        } else if !loaded {
            LoadingState { Task { await load() } }
        } else if let rgb, !rgb.available || rgb.isConflict {
            unavailableCard(rgb)
        } else {
            if let lock = syncLock {
                ProCard(feature: lock.text)
            }
            SectionHeader("rgb.section.look")
            lookCard
                .disabled(syncLock != nil)
            SectionHeader("rgb.section.profiles")
            if missesProfilePermission {
                PermissionCard(permission: .rgb, isChecking: controller.state == .connecting) {
                    Task {
                        await controller.refresh()
                        await load()
                    }
                }
            }
            profilesCard
                .disabled(syncLock != nil)
            SectionHeader("rgb.section.devices")
            devicesCard
        }
    }

    // MARK: - Cards

    /// `available: false` or another program holds the lighting: text by `reasonCode` (error-texts.md, 3.9).
    private func unavailableCard(_ rgb: RgbStatus) -> some View {
        VStack(alignment: .leading, spacing: Theme.Space.m) {
            ErrorCard(message: MonitoringView.text(ErrorPresentation.rgbUnavailable(rgb))) {
                Task { await load() }
            }
            Text("rgb.unavailable.help")
                .font(.caption)
                .foregroundStyle(Theme.textMuted)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var lookCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 14) {
                ColorPicker(selection: $color, supportsOpacity: false) { Text("rgb.color") }
                VStack(alignment: .leading, spacing: 4) {
                    Text("rgb.brightness \(Int(brightness))")
                    Slider(value: $brightness, in: 0...100, step: 5) {
                        Text("rgb.brightness \(Int(brightness))")
                    }
                    .tint(Theme.violet)
                }
                Picker(selection: $effect) {
                    Text("rgb.effect.static").tag("static")
                    Text("rgb.effect.off").tag("off")
                    ForEach(deviceEffects, id: \.self) { name in
                        Text(verbatim: name).tag(name)
                    }
                } label: {
                    Text("rgb.effect")
                }
                .pickerStyle(.menu)
                .tint(Theme.cyan)
                Text("rgb.effect.help")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    NeonButton(title: "rgb.apply", systemImage: "wand.and.stars") {
                        Task { await apply() }
                    }
                    .disabled(busy)
                    Button {
                        newName = ""
                        linkTo = ""
                        saving = true
                    } label: {
                        Text("rgb.saveAsProfile")
                    }
                    .foregroundStyle(Theme.cyan)
                    .disabled(!controller.can("rgb.profiles.upsert"))
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
                    Text("rgb.profiles.empty")
                        .font(.caption)
                        .foregroundStyle(Theme.textMuted)
                        .fixedSize(horizontal: false, vertical: true)
                }
                ForEach(profiles) { profile in
                    HStack(spacing: 12) {
                        Circle()
                            .fill(Color(hex: profile.color) ?? .gray)
                            .frame(width: 22, height: 22)
                            .overlay(Circle().stroke(Theme.textMuted.opacity(0.4)))
                            .accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(verbatim: profile.name).font(.subheadline.weight(.semibold))
                            Text(verbatim: profileCaption(profile))
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
                        .accessibilityLabel(Text("rgb.profile.apply"))
                    }
                    .contextMenu {
                        Button { load(profile) } label: { Text("rgb.profile.loadToEditor") }
                        Button(role: .destructive) {
                            Task { await delete(profile) }
                        } label: {
                            Text("rgb.profile.delete")
                        }
                        .disabled(!controller.can("rgb.profiles.delete"))
                    }
                }
                Text("rgb.profiles.hint")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var devicesCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 8) {
                if devices.isEmpty {
                    Text("rgb.empty.body")
                        .font(.caption)
                        .foregroundStyle(Theme.textMuted)
                        .fixedSize(horizontal: false, vertical: true)
                }
                ForEach(devices) { device in
                    HStack {
                        Image(systemName: device.symbol).foregroundStyle(Theme.violet).frame(width: 24)
                            .accessibilityHidden(true)
                        Text(verbatim: device.name).font(.subheadline)
                        Spacer()
                        Text("rgb.leds \(device.leds)").font(.caption).foregroundStyle(Theme.textMuted)
                    }
                    .accessibilityElement(children: .combine)
                }
                Text("rgb.skydimo")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var saveSheet: some View {
        NavigationStack {
            Form {
                if !controller.profiles.isEmpty {
                    Section {
                        Picker(selection: $linkTo) {
                            Text("rgb.save.noLink").tag("")
                            ForEach(controller.profiles) { profile in
                                Text(verbatim: profile.name).tag(profile.id)
                            }
                        } label: {
                            Text("rgb.save.linkTo")
                        }
                    } footer: {
                        Text("rgb.save.linkFooter")
                    }
                }
                if linkTo.isEmpty {
                    Section {
                        TextField(text: $newName) { Text("rgb.save.namePlaceholder") }
                    } header: {
                        Text("rgb.save.name")
                    }
                }
            }
            .navigationTitle(Text("rgb.save.title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button { saving = false } label: { Text("common.cancel") }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button {
                        saving = false
                        Task { await saveProfile() }
                    } label: {
                        Text("common.save")
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
        case "static": String(localized: "rgb.effect.static.short")
        case "off": String(localized: "rgb.effect.off.short")
        default: profile.effect
        }
        var parts = ["\(effectName) · \(profile.brightness)%"]
        if let linked = controller.profiles.first(where: { $0.id == profile.id }) {
            parts.append(String(localized: "rgb.linkedWith \(linked.name)"))
        }
        return parts.joined(separator: " · ")
    }

    // MARK: - Actions

    private func load() async {
        do {
            let result = try await controller.fetch("rgb.status")
            rgb = RgbStatus(result: result)
            loadError = nil
            devices = (result["devices"] as? [[String: Any]] ?? []).compactMap(RgbDeviceInfo.init)
            profiles = (result["profiles"] as? [[String: Any]] ?? []).compactMap(RgbProfileInfo.init)
            if !deviceEffects.contains(effect), !["static", "off"].contains(effect) {
                effect = "static"
            }
        } catch {
            rgb = nil
            loadError = message(error)
        }
        loaded = true
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
            status = String(localized: "rgb.profile.saved \(name)")
            await load()
        } catch {
            status = message(error)
        }
    }

    private func delete(_ profile: RgbProfileInfo) async {
        do {
            _ = try await controller.fetch("rgb.profiles.delete", payload: ["profileId": profile.id])
            status = String(localized: "rgb.profile.deleted \(profile.name)")
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
            return String(localized: "rgb.applied \(applied) \(items.count)")
        }
        let names = failed.compactMap { $0["name"] as? String }.joined(separator: ", ")
        let why = failed.first?["error"] as? String ?? ""
        return String(localized: "rgb.appliedPartial \(applied) \(items.count) \(names) \(why)")
    }

    private func message(_ error: Error) -> String {
        MonitoringView.describe(error)
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

    /// Lower-case Russian letters (U+0430–U+044F, U+0451) as escapes: string literals here stay ASCII.
    private static let cyrillic: [Character: String] = [
        "\u{0430}": "a", "\u{0431}": "b", "\u{0432}": "v", "\u{0433}": "g", "\u{0434}": "d", "\u{0435}": "e",
        "\u{0451}": "e", "\u{0436}": "zh", "\u{0437}": "z", "\u{0438}": "i", "\u{0439}": "y", "\u{043A}": "k",
        "\u{043B}": "l", "\u{043C}": "m", "\u{043D}": "n", "\u{043E}": "o", "\u{043F}": "p", "\u{0440}": "r",
        "\u{0441}": "s", "\u{0442}": "t", "\u{0443}": "u", "\u{0444}": "f", "\u{0445}": "h", "\u{0446}": "ts",
        "\u{0447}": "ch", "\u{0448}": "sh", "\u{0449}": "sch", "\u{044A}": "", "\u{044B}": "y", "\u{044C}": "",
        "\u{044D}": "e", "\u{044E}": "yu", "\u{044F}": "ya",
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
