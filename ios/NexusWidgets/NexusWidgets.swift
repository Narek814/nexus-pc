import ActivityKit
import SwiftUI
import WidgetKit

// Widgets cannot reach the device key with a free Apple ID (no App Groups / shared Keychain), so
// they do not talk to the PC themselves: each button opens the app, which performs the action.

private enum WidgetTheme {
    static let background = Color(red: 7 / 255, green: 7 / 255, blue: 11 / 255)
    static let surface = Color(red: 22 / 255, green: 22 / 255, blue: 30 / 255)
    static let violet = Color(red: 155 / 255, green: 92 / 255, blue: 1)
    static let cyan = Color(red: 0, green: 220 / 255, blue: 1)
    static let accent = LinearGradient(colors: [violet, cyan], startPoint: .topLeading, endPoint: .bottomTrailing)
}

struct QuickEntry: TimelineEntry {
    let date: Date
}

struct QuickProvider: TimelineProvider {
    func placeholder(in context: Context) -> QuickEntry { QuickEntry(date: Date()) }

    func getSnapshot(in context: Context, completion: @escaping (QuickEntry) -> Void) {
        completion(QuickEntry(date: Date()))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<QuickEntry>) -> Void) {
        completion(Timeline(entries: [QuickEntry(date: Date())], policy: .never))
    }
}

struct QuickActionsView: View {
    @Environment(\.widgetFamily) private var family
    let entry: QuickEntry

    var body: some View {
        switch family {
        case .accessoryCircular:
            ZStack {
                AccessoryWidgetBackground()
                Image(systemName: "lock.fill").font(.title3)
            }
            .widgetURL(DeepLink.lock)
        case .systemSmall:
            VStack(alignment: .leading, spacing: 8) {
                Text("NEXUS PC").font(.system(size: 15, weight: .heavy, design: .rounded)).foregroundStyle(WidgetTheme.accent)
                Spacer()
                Image(systemName: "desktopcomputer").font(.largeTitle).foregroundStyle(WidgetTheme.cyan)
                Text("Command Center").font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .widgetURL(DeepLink.open)
        default:
            VStack(alignment: .leading, spacing: 10) {
                Text("NEXUS PC").font(.system(size: 15, weight: .heavy, design: .rounded)).foregroundStyle(WidgetTheme.accent)
                HStack(spacing: 10) {
                    tile("Lock", "lock.fill", WidgetTheme.cyan, DeepLink.lock)
                    tile("Gaming", "gamecontroller.fill", WidgetTheme.violet, DeepLink.profile("gaming"))
                    tile("Shut down", "power", .red, DeepLink.shutdown)
                }
            }
        }
    }

    private func tile(_ title: String, _ icon: String, _ tint: Color, _ url: URL) -> some View {
        Link(destination: url) {
            VStack(spacing: 6) {
                Image(systemName: icon).font(.title2)
                Text(title).font(.caption2.weight(.semibold))
            }
            .foregroundStyle(tint)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(WidgetTheme.surface, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
    }
}

struct QuickActionsWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "NexusQuickActions", provider: QuickProvider()) { entry in
            QuickActionsView(entry: entry)
                .containerBackground(WidgetTheme.background, for: .widget)
        }
        .configurationDisplayName("Nexus PC")
        .description("Quick actions for your PC. Each opens Nexus PC and runs the action.")
        .supportedFamilies([.systemSmall, .systemMedium, .accessoryCircular])
    }
}

struct PowerCountdownLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: PowerCountdownAttributes.self) { context in
            HStack(spacing: 14) {
                Image(systemName: context.attributes.action == "restart" ? "arrow.clockwise" : "power")
                    .font(.title2)
                    .foregroundStyle(WidgetTheme.cyan)
                VStack(alignment: .leading, spacing: 2) {
                    Text(context.attributes.action == "restart" ? "Restarting \(context.attributes.pcName)" : "Shutting down \(context.attributes.pcName)")
                        .font(.headline)
                    Text(timerInterval: Date()...max(Date(), context.state.endsAt), countsDown: true)
                        .font(.title2.monospacedDigit())
                        .foregroundStyle(WidgetTheme.accent)
                }
                Spacer()
                Link(destination: DeepLink.cancel) {
                    Text("Cancel").font(.subheadline.weight(.semibold))
                }
            }
            .padding()
            .activityBackgroundTint(WidgetTheme.background)
            .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Image(systemName: context.attributes.action == "restart" ? "arrow.clockwise" : "power")
                        .foregroundStyle(WidgetTheme.cyan)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(timerInterval: Date()...max(Date(), context.state.endsAt), countsDown: true)
                        .monospacedDigit()
                        .frame(width: 64)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    HStack {
                        Text(context.attributes.pcName).font(.caption)
                        Spacer()
                        Link("Cancel", destination: DeepLink.cancel)
                    }
                }
            } compactLeading: {
                Image(systemName: "power").foregroundStyle(WidgetTheme.cyan)
            } compactTrailing: {
                Text(timerInterval: Date()...max(Date(), context.state.endsAt), countsDown: true)
                    .monospacedDigit()
                    .frame(width: 44)
            } minimal: {
                Image(systemName: "power").foregroundStyle(WidgetTheme.cyan)
            }
            .widgetURL(DeepLink.open)
        }
    }
}

@main
struct NexusWidgetBundle: WidgetBundle {
    var body: some Widget {
        QuickActionsWidget()
        PowerCountdownLiveActivity()
    }
}
