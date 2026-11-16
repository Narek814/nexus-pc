import ActivityKit
import Foundation

enum LiveActivities {
    /// Shows the countdown on the Lock Screen and in the Dynamic Island. No-op if the person turned
    /// Live Activities off for Nexus PC.
    static func startCountdown(pcName: String, action: String, endsAt: Date) {
        guard ActivityAuthorizationInfo().areActivitiesEnabled else {
            return
        }
        Task {
            await endAll()
            let content = ActivityContent(state: PowerCountdownAttributes.ContentState(endsAt: endsAt), staleDate: endsAt)
            _ = try? Activity.request(attributes: PowerCountdownAttributes(pcName: pcName, action: action), content: content)
        }
    }

    static func endAll() async {
        for activity in Activity<PowerCountdownAttributes>.activities {
            await activity.end(nil, dismissalPolicy: .immediate)
        }
    }

    /// Countdowns that already ran out are removed when the app opens.
    static func endExpired() async {
        for activity in Activity<PowerCountdownAttributes>.activities where activity.content.state.endsAt < Date() {
            await activity.end(nil, dismissalPolicy: .immediate)
        }
    }
}
