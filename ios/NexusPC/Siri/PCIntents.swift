import AppIntents
import Foundation
import NexusKit

// Siri and Shortcuts actions (P3). They run inside the app process, which holds the device key,
// so they work with a free Apple ID (no App Groups needed). Everything goes through the same
// authenticated, permission-checked protocol as the app.

struct PCStatusIntent: AppIntent {
    static var title: LocalizedStringResource = "PC Status"
    static var description = IntentDescription("Tells whether your PC is online and how long it has been running.")

    func perform() async throws -> some IntentResult & ProvidesDialog {
        let (name, uptime) = try await PCSession.withPC { pc, connection -> (String, Int) in
            let status = try await connection.request("pc.status").unwrap()
            return (status["pcName"] as? String ?? pc.name, status["uptimeSeconds"] as? Int ?? 0)
        }
        let hours = uptime / 3600
        let minutes = uptime % 3600 / 60
        // Interpolated directly so the sentence can be translated (Localizable.xcstrings).
        return .result(dialog: "\(name) is online, running for \(hours) h \(minutes) min.")
    }
}

struct WakePCIntent: AppIntent {
    static var title: LocalizedStringResource = "Turn On PC"
    static var description = IntentDescription("Sends a Wake-on-LAN signal to your PC. Works on the same network as the PC.")

    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let pc = PairingStore.load().first else {
            throw NexusError.connection("Nexus PC is not paired with a PC yet.")
        }
        guard pc.wake?.isEmpty == false else {
            return .result(dialog: "Open Nexus PC once while \(pc.name) is on, so it can learn how to wake it.")
        }
        let sent = await WakeService.send(to: pc)
        if sent > 0 {
            return .result(dialog: "Sent the wake signal to \(pc.name). It usually starts in 30 to 60 seconds.")
        }
        return .result(dialog: "Could not send the wake signal. Is your iPhone on the same network as \(pc.name)?")
    }
}

struct LockPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Lock PC"
    static var description = IntentDescription("Locks the Windows session on your PC.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    func perform() async throws -> some IntentResult & ProvidesDialog {
        let name = try await PCSession.withPC { pc, connection -> String in
            _ = try await connection.request("power.lock").unwrap()
            return pc.name
        }
        return .result(dialog: "\(name) is locked.")
    }
}

struct SleepPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Put PC to Sleep"
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    func perform() async throws -> some IntentResult & ProvidesDialog {
        let name = try await PCSession.withPC { pc, connection -> String in
            _ = try await connection.request("power.sleep").unwrap()
            return pc.name
        }
        return .result(dialog: "\(name) is going to sleep.")
    }
}

/// Asks for confirmation, then shuts down after a countdown (default 5 minutes): Windows shows its
/// notice and it can be cancelled.
struct ShutdownPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Shut Down PC"
    static var description = IntentDescription("Shuts down your PC after a countdown. Say “Cancel PC shutdown” to stop it.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    // 5 minutes, not 1: on a real PC one minute was too short to cancel after a voice command.
    @Parameter(title: "In minutes", default: 5, inclusiveRange: (1, 1440))
    var minutes: Int

    func perform() async throws -> some IntentResult & ProvidesDialog {
        // Siri always asks before scheduling a shutdown.
        let pcName = PairingStore.load().first?.name ?? "your PC"
        try await requestConfirmation(result: .result(dialog: "Shut down \(pcName) in \(minutes) min?"))
        let name = try await PCSession.withPC { pc, connection -> String in
            _ = try await connection.request("power.shutdown", payload: ["delaySeconds": minutes * 60], confirmed: true).unwrap()
            return pc.name
        }
        return .result(dialog: "\(name) will shut down in \(minutes) min. Say “Cancel PC shutdown” to stop it.")
    }
}

struct RestartPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Restart PC"
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    @Parameter(title: "In minutes", default: 5, inclusiveRange: (1, 1440))
    var minutes: Int

    func perform() async throws -> some IntentResult & ProvidesDialog {
        let pcName = PairingStore.load().first?.name ?? "your PC"
        try await requestConfirmation(result: .result(dialog: "Restart \(pcName) in \(minutes) min?"))
        let name = try await PCSession.withPC { pc, connection -> String in
            _ = try await connection.request("power.restart", payload: ["delaySeconds": minutes * 60], confirmed: true).unwrap()
            return pc.name
        }
        return .result(dialog: "\(name) will restart in \(minutes) min.")
    }
}

struct CancelShutdownIntent: AppIntent {
    static var title: LocalizedStringResource = "Cancel PC Shutdown"

    func perform() async throws -> some IntentResult & ProvidesDialog {
        let name = try await PCSession.withPC { pc, connection -> String in
            _ = try await connection.request("power.cancel").unwrap()
            return pc.name
        }
        await LiveActivities.endAll()
        return .result(dialog: "Shutdown of \(name) is cancelled.")
    }
}

struct RunProfileIntent: AppIntent {
    static var title: LocalizedStringResource = "Run Profile"
    static var description = IntentDescription("Starts every app of a profile on your PC, e.g. Gaming.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    @Parameter(title: "Profile")
    var profile: ProfileEntity

    func perform() async throws -> some IntentResult & ProvidesDialog {
        let started = try await PCSession.withPC { _, connection -> Int in
            let result = try await connection.request("profiles.run", payload: ["profileId": profile.id]).unwrap()
            return result["started"] as? Int ?? 0
        }
        return .result(dialog: "\(profile.name): started \(started) apps.")
    }
}

struct LaunchAppIntent: AppIntent {
    static var title: LocalizedStringResource = "Open App on PC"
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    @Parameter(title: "App")
    var app: PCAppEntity

    func perform() async throws -> some IntentResult & ProvidesDialog {
        try await PCSession.withPC { _, connection in
            _ = try await connection.request("apps.launch", payload: ["appId": app.id]).unwrap()
        }
        return .result(dialog: "Opened \(app.name).")
    }
}

// MARK: - Entities (profiles and apps come from the PC's own list)

struct ProfileEntity: AppEntity {
    static var typeDisplayRepresentation: TypeDisplayRepresentation = "Profile"
    static var defaultQuery = ProfileQuery()

    let id: String
    let name: String

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)")
    }
}

struct ProfileQuery: EntityStringQuery {
    func entities(for identifiers: [String]) async throws -> [ProfileEntity] {
        try await suggestedEntities().filter { identifiers.contains($0.id) }
    }

    func entities(matching string: String) async throws -> [ProfileEntity] {
        try await suggestedEntities().filter { $0.name.localizedCaseInsensitiveContains(string) || $0.id.contains(string.lowercased()) }
    }

    func suggestedEntities() async throws -> [ProfileEntity] {
        try await PCSession.withPC { _, connection in
            let list = try await connection.request("profiles.list").unwrap()
            return (list["profiles"] as? [[String: Any]] ?? []).compactMap { item in
                guard let id = item["id"] as? String, let name = item["name"] as? String else { return nil }
                return ProfileEntity(id: id, name: name)
            }
        }
    }
}

struct PCAppEntity: AppEntity {
    static var typeDisplayRepresentation: TypeDisplayRepresentation = "PC App"
    static var defaultQuery = PCAppQuery()

    let id: String
    let name: String

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)")
    }
}

struct PCAppQuery: EntityStringQuery {
    func entities(for identifiers: [String]) async throws -> [PCAppEntity] {
        try await suggestedEntities().filter { identifiers.contains($0.id) }
    }

    func entities(matching string: String) async throws -> [PCAppEntity] {
        try await suggestedEntities().filter { $0.name.localizedCaseInsensitiveContains(string) }
    }

    func suggestedEntities() async throws -> [PCAppEntity] {
        try await PCSession.withPC { _, connection in
            let list = try await connection.request("apps.list").unwrap()
            return (list["apps"] as? [[String: Any]] ?? []).compactMap { item in
                guard let id = item["id"] as? String, let name = item["name"] as? String else { return nil }
                return PCAppEntity(id: id, name: name)
            }
        }
    }
}

// MARK: - Siri phrases. English here; Russian ones are in AppShortcuts.xcstrings (Siri uses the phrases of
// the Siri language, so with Siri set to Russian these work even on an English iPhone).

struct NexusShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: RunProfileIntent(),
            phrases: [
                "Start \(\.$profile) in \(.applicationName)",
                "Run \(\.$profile) with \(.applicationName)",
                "Start \(\.$profile) mode in \(.applicationName)",
            ],
            shortTitle: "Run Profile",
            systemImageName: "gamecontroller.fill")
        AppShortcut(
            intent: WakePCIntent(),
            phrases: ["Turn on my PC with \(.applicationName)", "Wake my PC with \(.applicationName)"],
            shortTitle: "Turn On PC",
            systemImageName: "power.circle")
        AppShortcut(
            intent: LockPCIntent(),
            phrases: ["Lock my PC with \(.applicationName)", "Lock PC in \(.applicationName)"],
            shortTitle: "Lock PC",
            systemImageName: "lock.fill")
        AppShortcut(
            intent: ShutdownPCIntent(),
            phrases: ["Shut down my PC with \(.applicationName)", "Turn off my PC with \(.applicationName)"],
            shortTitle: "Shut Down PC",
            systemImageName: "power")
        AppShortcut(
            intent: CancelShutdownIntent(),
            phrases: ["Cancel PC shutdown in \(.applicationName)", "Cancel shutdown with \(.applicationName)"],
            shortTitle: "Cancel Shutdown",
            systemImageName: "xmark.circle")
        AppShortcut(
            intent: PCStatusIntent(),
            phrases: ["How is my PC in \(.applicationName)", "Check my PC with \(.applicationName)"],
            shortTitle: "PC Status",
            systemImageName: "desktopcomputer")
        AppShortcut(
            intent: SleepPCIntent(),
            phrases: ["Put my PC to sleep with \(.applicationName)", "Sleep my PC with \(.applicationName)"],
            shortTitle: "Sleep PC",
            systemImageName: "moon.fill")
        AppShortcut(
            intent: RestartPCIntent(),
            phrases: ["Restart my PC with \(.applicationName)", "Reboot my PC with \(.applicationName)"],
            shortTitle: "Restart PC",
            systemImageName: "arrow.clockwise")
        AppShortcut(
            intent: LaunchAppIntent(),
            phrases: ["Open \(\.$app) on my PC with \(.applicationName)", "Launch \(\.$app) with \(.applicationName)"],
            shortTitle: "Open App on PC",
            systemImageName: "app.badge")
    }
}
