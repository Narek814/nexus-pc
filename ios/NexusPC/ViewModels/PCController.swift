import AppIntents
import Foundation
import NexusKit
import Observation

struct PCStatus: Equatable {
    var name: String
    var os: String
    var uptimeSeconds: Int
    var userSessionAvailable: Bool
}

struct RemoteApp: Identifiable, Equatable {
    let id: String
    let name: String
    let canClose: Bool
}

struct RemoteProfile: Identifiable, Equatable {
    let id: String
    let name: String
    let appCount: Int
}

enum PowerCommand: Equatable {
    case shutdown(delaySeconds: Int)
    case restart(delaySeconds: Int)
    case sleep
    case lock
    case cancel
}

/// State of the paired PC and every action on it. One connection, re-established when needed.
@MainActor
@Observable
final class PCController {
    enum ConnectionState: Equatable {
        case notPaired
        case connecting
        case online
        case offline(String)
    }

    private(set) var pc: PairedPC?
    private(set) var state: ConnectionState = .notPaired
    private(set) var status: PCStatus?
    private(set) var capabilities: Set<String> = []
    private(set) var apps: [RemoteApp] = []
    private(set) var profiles: [RemoteProfile] = []
    private(set) var pendingPower: String?
    private(set) var metrics: MetricsReading?
    /// This iPhone's key fingerprint for the paired PC, as the agent's "devices" shows it (T3).
    private(set) var deviceKeyFingerprint: String?
    var banner: String?

    private var connection: NexusConnection?
    private var connecting = false

    init() {
        pc = PairingStore.load().first
        state = pc == nil ? .notPaired : .offline("Подключение…")
        deviceKeyFingerprint = Self.keyFingerprint(for: pc)
    }

    func paired(_ newPC: PairedPC) {
        pc = newPC
        deviceKeyFingerprint = Self.keyFingerprint(for: newPC)
        connection?.close()
        connection = nil
        Task { await refresh() }
    }

    func forget() {
        guard let pc else {
            return
        }
        connection?.close()
        connection = nil
        PairingStore.forget(pc)
        self.pc = nil
        deviceKeyFingerprint = nil
        status = nil
        apps = []
        profiles = []
        state = .notPaired
    }

    /// Connects if needed, then reloads status, apps and profiles.
    func refresh() async {
        guard pc != nil else {
            return
        }
        await LiveActivities.endExpired()
        do {
            let connection = try await ensureConnection()
            try await load(using: connection)
            state = .online
        } catch {
            drop(error)
        }
    }

    func perform(_ command: PowerCommand) async {
        let (type, payload, confirmed): (String, [String: Any], Bool) = switch command {
        case let .shutdown(delay): ("power.shutdown", ["delaySeconds": delay], true)
        case let .restart(delay): ("power.restart", ["delaySeconds": delay], true)
        case .sleep: ("power.sleep", [:], false)
        case .lock: ("power.lock", [:], false)
        case .cancel: ("power.cancel", [:], false)
        }
        await run(type, payload: payload, confirmed: confirmed) { [weak self] result in
            guard let self else { return }
            switch command {
            case .shutdown, .restart:
                let delay = result["delaySeconds"] as? Int ?? 0
                let verb: String
                if case .restart = command {
                    verb = "Перезагрузка"
                } else {
                    verb = "Выключение"
                }
                self.pendingPower = delay > 0 ? "\(verb) через \(Self.duration(delay))" : nil
                self.banner = delay > 0 ? "\(verb) запланировано: через \(Self.duration(delay))" : "\(verb)…"
                if delay > 0 {
                    let action: String
                    if case .restart = command {
                        action = "restart"
                    } else {
                        action = "shutdown"
                    }
                    LiveActivities.startCountdown(pcName: self.status?.name ?? self.pc?.name ?? "PC", action: action, endsAt: Date().addingTimeInterval(TimeInterval(delay)))
                }
            case .sleep:
                self.banner = "ПК уходит в сон"
                self.state = .offline("ПК спит")
            case .lock:
                self.banner = "ПК заблокирован"
            case .cancel:
                self.pendingPower = nil
                self.banner = "Выключение отменено"
                Task { await LiveActivities.endAll() }
            }
        }
    }

    func launch(_ app: RemoteApp) async {
        await run("apps.launch", payload: ["appId": app.id]) { [weak self] _ in
            self?.banner = "Запущено: \(app.name)"
        }
    }

    /// From a widget link: the profile list may not be loaded yet.
    func runProfile(id: String) async {
        if profiles.isEmpty {
            await refresh()
        }
        if let profile = profiles.first(where: { $0.id == id }) {
            await runProfile(profile)
        } else {
            banner = "Профиль «\(id)» не найден на ПК"
        }
    }

    func runProfile(_ profile: RemoteProfile) async {
        await run("profiles.run", payload: ["profileId": profile.id]) { [weak self] result in
            let started = result["started"] as? Int ?? 0
            let lighting = switch result["lighting"] as? String {
            case "applied": " · подсветка включена"
            case "unavailable": " · подсветка: OpenRGB не запущен"
            case "noDevices": " · подсветка: устройства не ответили"
            default: ""
            }
            self?.banner = "\(profile.name): запущено \(started) из \(profile.appCount)\(lighting)"
        }
    }

    /// Read-only request for other screens (Monitoring). Reconnects once if authentication expired.
    func fetch(_ type: String, payload: [String: Any] = [:], confirmed: Bool = false, timeout: TimeInterval = 15) async throws -> [String: Any] {
        do {
            var response = try await ensureConnection().request(type, payload: payload, confirmed: confirmed, timeout: timeout)
            if response.errorCode == "auth.required" {
                connection?.close()
                connection = nil
                response = try await ensureConnection().request(type, payload: payload, confirmed: confirmed, timeout: timeout)
            }
            return try response.unwrap()
        } catch let error as NexusError {
            if case .server = error {
                throw error
            }
            drop(error)
            throw error
        }
    }

    func can(_ command: String) -> Bool {
        capabilities.contains(command)
    }

    /// Fingerprint of the public half of this phone's key for the PC; the private key stays in the Secure Enclave.
    private static func keyFingerprint(for pc: PairedPC?) -> String? {
        guard let pc, let signer = try? PairingStore.signer(for: pc) else {
            return nil
        }
        return DeviceCrypto.deviceKeyFingerprint(publicKey: signer.publicKeySPKI)
    }

    // MARK: - Internals

    private func run(_ type: String, payload: [String: Any] = [:], confirmed: Bool = false, onSuccess: @escaping ([String: Any]) -> Void) async {
        do {
            let connection = try await ensureConnection()
            var response = try await connection.request(type, payload: payload, confirmed: confirmed)
            if response.errorCode == "auth.required" {
                // Authentication expired (1 h) or the device was revoked: reconnect once.
                self.connection?.close()
                self.connection = nil
                response = try await ensureConnection().request(type, payload: payload, confirmed: confirmed)
            }
            onSuccess(try response.unwrap())
        } catch let error as NexusError {
            if case .server = error {
                banner = error.localizedDescription
            } else {
                drop(error)
            }
        } catch {
            drop(error)
        }
    }

    private func ensureConnection() async throws -> NexusConnection {
        if let connection {
            return connection
        }
        guard let pc else {
            throw NexusError.connection("ПК не подключён.")
        }
        state = .connecting
        let connection = try await PCSession.connect(pc)
        self.connection = connection
        return connection
    }

    private func load(using connection: NexusConnection) async throws {
        let hello = try await connection.request("system.hello").unwrap()
        capabilities = Set(hello["capabilities"] as? [String] ?? [])
        let s = try await connection.request("pc.status").unwrap()
        status = PCStatus(
            name: s["pcName"] as? String ?? pc?.name ?? "ПК",
            os: s["os"] as? String ?? "",
            uptimeSeconds: s["uptimeSeconds"] as? Int ?? 0,
            userSessionAvailable: s["userSession"] as? String == "available")
        learnAddresses(from: s)

        if capabilities.contains("apps.list"), let list = try? await connection.request("apps.list").unwrap() {
            apps = (list["apps"] as? [[String: Any]] ?? []).compactMap { item in
                guard let id = item["id"] as? String, let name = item["name"] as? String else { return nil }
                return RemoteApp(id: id, name: name, canClose: item["canClose"] as? Bool ?? false)
            }
        } else {
            apps = []
        }
        if capabilities.contains("profiles.list"), let list = try? await connection.request("profiles.list").unwrap() {
            profiles = (list["profiles"] as? [[String: Any]] ?? []).compactMap { item in
                guard let id = item["id"] as? String, let name = item["name"] as? String else { return nil }
                return RemoteProfile(id: id, name: name, appCount: (item["appIds"] as? [Any])?.count ?? 0)
            }
        } else {
            profiles = []
        }
        if capabilities.contains("metrics.snapshot"), let snapshot = try? await connection.request("metrics.snapshot").unwrap() {
            metrics = MetricsReading(snapshot)
        }
        // Lets Siri match spoken profile and app names ("Start Gaming in Nexus PC").
        NexusShortcuts.updateAppShortcutParameters()
    }

    /// Remembers the PC's current addresses (new first, e.g. a new DHCP address or Tailscale) and its
    /// Wake-on-LAN targets, so it can be reached and woken later.
    private func learnAddresses(from status: [String: Any]) {
        guard var current = pc else {
            return
        }
        if let hosts = status["hosts"] as? [String], !hosts.isEmpty {
            var merged: [String] = []
            for host in hosts + current.hosts where !merged.contains(host) {
                merged.append(host)
            }
            current.hosts = Array(merged.prefix(8))
        }
        if let wake = status["wake"] as? [[String: Any]] {
            let targets = wake.compactMap { item -> WakeTarget? in
                guard let mac = item["mac"] as? String, let broadcast = item["broadcast"] as? String else { return nil }
                return WakeTarget(mac: mac, broadcast: broadcast, wireless: item["wireless"] as? Bool ?? false)
            }
            if !targets.isEmpty {
                current.wake = targets
            }
        }
        if current != pc {
            pc = current
            PairingStore.update(current)
        }
    }

    var canWake: Bool {
        !(pc?.wake ?? []).isEmpty
    }

    var wakeIsWireless: Bool {
        (pc?.wake ?? []).allSatisfy(\.wireless)
    }

    /// Sends the wake signal, then keeps trying to connect for up to 2 minutes.
    func wake() async {
        guard let pc else {
            return
        }
        let sent = await WakeService.send(to: pc)
        guard sent > 0 else {
            banner = "Не удалось отправить сигнал. iPhone в той же сети, что и ПК?"
            return
        }
        banner = "Сигнал включения отправлен"
        state = .offline("Включаю… ПК обычно загружается 30–60 секунд.")
        for _ in 0..<12 {
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            await refresh()
            if state == .online {
                banner = "\(status?.name ?? pc.name) в сети"
                return
            }
            state = .offline("Включаю… ПК обычно загружается 30–60 секунд.")
        }
        state = .offline(wakeIsWireless
            ? "ПК не включился. Он подключён по Wi-Fi — включение по сети обычно работает только по кабелю (docs/REMOTE.md)."
            : "ПК не включился. Проверьте Wake-on-LAN в BIOS и настройках сетевой карты (docs/REMOTE.md).")
    }

    private func drop(_ error: Error) {
        connection?.close()
        connection = nil
        state = .offline((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)
    }

    static func duration(_ seconds: Int) -> String {
        seconds >= 60 ? "\(seconds / 60) мин" : "\(seconds) с"
    }
}
