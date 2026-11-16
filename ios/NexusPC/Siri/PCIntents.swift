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
        let text = try await PCSession.withPC { pc, connection -> String in
            let status = try await connection.request("pc.status").unwrap()
            let uptime = status["uptimeSeconds"] as? Int ?? 0
            let name = status["pcName"] as? String ?? pc.name
            return "\(name) is online, running for \(uptime / 3600) h \(uptime % 3600 / 60) min."
        }
        return .result(dialog: "\(text)")
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

/// Always with a countdown (at least 1 minute): Windows shows its notice and it can be cancelled.
struct ShutdownPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Shut Down PC"
    static var description = IntentDescription("Shuts down your PC after a countdown. Say “Cancel PC shutdown” to stop it.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    @Parameter(title: "In minutes", default: 1, inclusiveRange: (1, 1440))
    var minutes: Int

    func perform() async throws -> some IntentResult & ProvidesDialog {
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

    @Parameter(title: "In minutes", default: 1, inclusiveRange: (1, 1440))
    var minutes: Int

    func perform() async throws -> some IntentResult & ProvidesDialog {
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

// MARK: - Siri phrases (English: the owner's iPhone language; Russian can be added via AppShortcuts.xcstrings)

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
            phrases: ["Put my PC to sleep with \(.applicationName)"],
            shortTitle: "Sleep PC",
            systemImageName: "moon.fill")
        AppShortcut(
            intent: RestartPCIntent(),
            phrases: ["Restart my PC with \(.applicationName)"],
            shortTitle: "Restart PC",
            systemImageName: "arrow.clockwise")
        AppShortcut(
            intent: LaunchAppIntent(),
            phrases: ["Open \(\.$app) on my PC with \(.applicationName)"],
            shortTitle: "Open App on PC",
            systemImageName: "app.badge")
    }
}
