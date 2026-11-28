import Charts
import NexusKit
import SwiftUI

/// Monitoring (P4a): live gauges, history charts, disks, network. Values the PC cannot read are shown
/// as "—" with the reason (by `unavailableCodes`, PC text as fallback), never invented.
/// History 1 h / 24 h is Pro (`monitoring.extended`) on agents with plans.
struct MonitoringView: View {
    let controller: PCController

    @AppStorage("monitoring.interval") private var interval = 2.0
    @AppStorage("monitoring.showCpu") private var showCpu = true
    @AppStorage("monitoring.showGpu") private var showGpu = true
    @AppStorage("monitoring.showMemory") private var showMemory = true
    @AppStorage("monitoring.showGpuTemp") private var showGpuTemp = false
    @State private var reading: MetricsReading?
    @State private var range = "10m"
    @State private var points: [MetricsPoint] = []
    @State private var error: String?
    @State private var proSheet: ProLock?
    /// Metric whose reason is expanded by a tap.
    @State private var expandedReason: String?

    init(controller: PCController) {
        self.controller = controller
    }

    private var extendedLock: ProLock? {
        ProLock.check(controller.hello, feature: ProFeature.monitoringExtended)
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if let error {
                        Label {
                            Text(verbatim: error)
                        } icon: {
                            Image(systemName: "exclamationmark.triangle").accessibilityHidden(true)
                        }
                        .font(.footnote)
                        .foregroundStyle(.orange)
                    }
                    gauges
                    SectionHeader("monitor.section.history")
                    chartCard
                    SectionHeader("monitor.section.cpu")
                    cpuCard
                    if let gpus = reading?.gpus, !gpus.isEmpty {
                        SectionHeader("monitor.section.gpu")
                        ForEach(gpus) { gpuCard($0) }
                    }
                    SectionHeader("monitor.section.disks")
                    disksCard
                    SectionHeader("monitor.section.network")
                    networkCard
                    unavailableCard
                    settingsCard
                }
                .padding(20)
            }
        }
        .navigationTitle(Text("section.monitor"))
        .navigationBarTitleDisplayMode(.inline)
        .sheet(item: $proSheet) { lock in
            ProCardSheet(lock: lock)
        }
        .task(id: interval) {
            while !Task.isCancelled {
                await poll()
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
            }
        }
        .task(id: range) {
            while !Task.isCancelled {
                await loadHistory()
                try? await Task.sleep(nanoseconds: range == "10m" ? 10_000_000_000 : 60_000_000_000)
            }
        }
        .onChange(of: extendedLock) { _, lock in
            // The plan ended while 1 h / 24 h was shown: back to the Free range.
            if lock != nil, range != "10m" { range = "10m" }
        }
    }

    // MARK: - Sections

    private var gauges: some View {
        HStack(spacing: 12) {
            RingGauge(title: "CPU", value: reading?.cpuUsage, text: Format.percent(reading?.cpuUsage), color: Theme.violet)
            RingGauge(title: "GPU", value: reading?.gpus.first?.usage, text: Format.percent(reading?.gpus.first?.usage), color: Theme.cyan)
            RingGauge(title: "RAM", value: reading?.memoryUsage, text: Format.percent(reading?.memoryUsage), color: .pink)
            RingGauge(title: "GPU °C", value: reading?.gpus.first?.temperature.map { min($0, 100) }, text: Format.temperature(reading?.gpus.first?.temperature), color: .orange)
        }
    }

    @ViewBuilder
    private var rangePicker: some View {
        if let lock = extendedLock {
            // Free: "1 h" and "24 h" never switch the range, they open ProCard.
            HStack(spacing: Theme.Space.xs) {
                PlainSegment(Text("monitor.range.10m"), isSelected: true) { range = "10m" }
                ProSegment(Text("monitor.range.1h")) { proSheet = lock }
                ProSegment(Text("monitor.range.24h")) { proSheet = lock }
            }
        } else {
            Picker(selection: $range) {
                Text("monitor.range.10m").tag("10m")
                Text("monitor.range.1h").tag("1h")
                Text("monitor.range.24h").tag("24h")
            } label: {
                Text("monitor.range")
            }
            .pickerStyle(.segmented)
        }
    }

    private var chartCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 12) {
                rangePicker

                if points.isEmpty {
                    VStack(alignment: .leading, spacing: Theme.Space.s) {
                        Text(range == "10m" ? LocalizedStringKey("monitor.chart.empty10m") : LocalizedStringKey("monitor.chart.emptyLong"))
                        Text("monitor.historyReset")
                    }
                    .font(.footnote)
                    .foregroundStyle(Theme.textMuted)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(minHeight: 160, alignment: .center)
                } else {
                    Chart {
                        ForEach(points) { p in
                            if showCpu, let v = p.cpu {
                                LineMark(x: .value("monitor.chart.time", p.time), y: .value("%", v), series: .value("monitor.chart.metric", "CPU"))
                                    .foregroundStyle(by: .value("monitor.chart.metric", "CPU"))
                            }
                            if showGpu, let v = p.gpu {
                                LineMark(x: .value("monitor.chart.time", p.time), y: .value("%", v), series: .value("monitor.chart.metric", "GPU"))
                                    .foregroundStyle(by: .value("monitor.chart.metric", "GPU"))
                            }
                            if showMemory, let v = p.memory {
                                LineMark(x: .value("monitor.chart.time", p.time), y: .value("%", v), series: .value("monitor.chart.metric", "RAM"))
                                    .foregroundStyle(by: .value("monitor.chart.metric", "RAM"))
                            }
                            if showGpuTemp, let v = p.gpuTemperature {
                                LineMark(x: .value("monitor.chart.time", p.time), y: .value("%", v), series: .value("monitor.chart.metric", "GPU °C"))
                                    .foregroundStyle(by: .value("monitor.chart.metric", "GPU °C"))
                            }
                        }
                    }
                    .chartForegroundStyleScale(["CPU": Theme.violet, "GPU": Theme.cyan, "RAM": Color.pink, "GPU °C": Color.orange])
                    .chartYScale(domain: 0...100)
                    .frame(height: 180)
                }
            }
        }
    }

    private var cpuCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 6) {
                Text("monitor.section.cpu").font(.headline).foregroundStyle(Theme.textPrimary)
                row("monitor.usage", Format.percent(reading?.cpuUsage), metric: "cpu.usage", isMissing: reading != nil && reading?.cpuUsage == nil)
                row("monitor.temperature", Format.temperature(reading?.cpuTemperature), metric: "cpu.temperature",
                    isMissing: reading != nil && reading?.cpuTemperature == nil)
                row("monitor.clock", reading?.cpuClock.map { Self.mhz($0) } ?? "—", metric: "cpu.clock",
                    isMissing: reading != nil && reading?.cpuClock == nil)
                if let threads = reading?.logicalProcessors {
                    row("monitor.threads", "\(threads)")
                }
            }
        }
    }

    private func gpuCard(_ gpu: MetricsReading.Gpu) -> some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 6) {
                Text(verbatim: gpu.name).font(.headline).foregroundStyle(Theme.textPrimary)
                row("monitor.usage", Format.percent(gpu.usage), metric: "gpu", isMissing: gpu.usage == nil)
                row("monitor.temperature", Format.temperature(gpu.temperature), metric: "gpu", isMissing: gpu.temperature == nil)
                row("monitor.gpu.memory", Self.ofTotal(Format.bytes(gpu.memoryUsed), Format.bytes(gpu.memoryTotal)))
                row("monitor.gpu.coreClock", gpu.coreClock.map { Self.mhz($0) } ?? "—", metric: "gpu", isMissing: gpu.coreClock == nil)
            }
        }
    }

    private var disksCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 10) {
                if let disks = reading?.disks, !disks.isEmpty {
                    ForEach(disks) { disk in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(verbatim: disk.name).foregroundStyle(Theme.textPrimary)
                                Spacer()
                                Text("monitor.disk.free \(Format.bytes(disk.free)) \(Format.bytes(disk.total))")
                                    .font(.caption)
                                    .foregroundStyle(disk.free / max(disk.total, 1) < 0.1 ? .orange : Theme.textMuted)
                            }
                            ProgressView(value: min(max(disk.usage / 100, 0), 1)).tint(disk.usage > 90 ? .orange : Theme.cyan)
                        }
                    }
                } else {
                    Text(verbatim: "—").foregroundStyle(Theme.textMuted)
                }
            }
        }
    }

    private var networkCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 6) {
                row("monitor.net.receive", Format.rate(reading?.receivePerSecond))
                row("monitor.net.send", Format.rate(reading?.sendPerSecond))
                row("monitor.memory", Self.ofTotal(Format.bytes(reading?.memoryUsed), Format.bytes(reading?.memoryTotal)),
                    metric: "memory", isMissing: reading != nil && reading?.memoryUsed == nil)
            }
        }
    }

    @ViewBuilder
    private var unavailableCard: some View {
        if let reading, !reading.unavailableMetrics.isEmpty {
            SectionHeader("monitor.unavailable")
            GlassCard {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(reading.unavailableMetrics, id: \.self) { key in
                        VStack(alignment: .leading, spacing: 2) {
                            Self.metricTitle(key).font(.subheadline.weight(.semibold)).foregroundStyle(Theme.textPrimary)
                            if let reason = reading.reason(for: key) {
                                Text(verbatim: Self.text(reason))
                                    .font(.caption)
                                    .foregroundStyle(Theme.textMuted)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
            }
        }
    }

    private var settingsCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionHeader("monitor.section.settings")
            GlassCard {
                VStack(alignment: .leading, spacing: 10) {
                    Picker(selection: $interval) {
                        Text("monitor.seconds \(1)").tag(1.0)
                        Text("monitor.seconds \(2)").tag(2.0)
                        Text("monitor.seconds \(5)").tag(5.0)
                    } label: {
                        Text("monitor.interval")
                    }
                    .pickerStyle(.segmented)
                    Toggle(isOn: $showCpu) { Text("monitor.chart.showCpu") }
                    Toggle(isOn: $showGpu) { Text("monitor.chart.showGpu") }
                    Toggle(isOn: $showMemory) { Text("monitor.chart.showMemory") }
                    Toggle(isOn: $showGpuTemp) { Text("monitor.chart.showGpuTemp") }
                }
                .tint(Theme.violet)
                .foregroundStyle(Theme.textPrimary)
            }
        }
    }

    /// One value row. A missing value ("—") with a known reason: the reason is in the VoiceOver label
    /// and hint, and a tap shows it under the row.
    @ViewBuilder
    private func row(_ title: LocalizedStringResource, _ value: String, metric: String? = nil, isMissing: Bool = false) -> some View {
        let reason = isMissing ? metric.flatMap { reading?.reason(for: $0) } : nil
        if let reason, let metric {
            let reasonText = Self.text(reason)
            Button {
                expandedReason = expandedReason == metric ? nil : metric
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    HStack {
                        Text(title).foregroundStyle(Theme.textMuted)
                        Spacer()
                        Text(verbatim: "—").foregroundStyle(Theme.textPrimary)
                        Image(systemName: "info.circle").foregroundStyle(Theme.textMuted).accessibilityHidden(true)
                    }
                    if expandedReason == metric {
                        Text(verbatim: reasonText)
                            .font(.caption)
                            .foregroundStyle(Theme.textMuted)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .font(.subheadline)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(Text(verbatim: String(format: String(localized: "monitor.unavailable.a11y"),
                                                      String(localized: title), reasonText)))
            .accessibilityHint(Text(verbatim: reasonText))
        } else {
            HStack {
                Text(title).foregroundStyle(Theme.textMuted)
                Spacer()
                Text(verbatim: value).monospacedDigit().foregroundStyle(Theme.textPrimary)
            }
            .font(.subheadline)
        }
    }

    // MARK: - Data

    private func poll() async {
        do {
            reading = MetricsReading(try await controller.fetch("metrics.snapshot"))
            error = nil
        } catch {
            self.error = Self.describe(error)
        }
    }

    private func loadHistory() async {
        if let payload = try? await controller.fetch("metrics.history", payload: ["range": range]) {
            points = MetricsPoint.parse(payload)
        }
    }

    // MARK: - Text

    static func text(_ reason: ErrorPresentation.ReasonText) -> String {
        switch reason {
        case let .key(key): Bundle.main.localizedString(forKey: key, value: nil, table: nil)
        case let .pcText(text): text
        }
    }

    /// Agent error by code (PC text only as fallback); other errors as the system describes them.
    @MainActor
    static func describe(_ error: Error) -> String {
        if case let .server(code, message, details)? = error as? NexusError {
            return PCController.bannerText(code: code, details: details, message: message, section: "monitoring") ?? ""
        }
        return (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }

    private static func mhz(_ value: Double) -> String {
        String(localized: "monitor.mhz \(Int(value))")
    }

    private static func ofTotal(_ part: String, _ total: String) -> String {
        String(localized: "monitor.ofTotal \(part) \(total)")
    }

    private static func metricTitle(_ key: String) -> Text {
        switch key {
        case "cpu.temperature": Text("monitor.metric.cpuTemperature")
        case "cpu.clock": Text("monitor.metric.cpuClock")
        case "cpu.usage": Text("monitor.metric.cpuUsage")
        case "memory": Text("monitor.memory")
        case "gpu": Text("monitor.section.gpu")
        default: Text(verbatim: key)
        }
    }
}

/// Neon ring gauge 0–100. Shows "—" without a ring when the value is unavailable.
struct RingGauge: View {
    let title: String
    let value: Double?
    let text: String
    let color: Color

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                Circle().stroke(Theme.surface, lineWidth: 7)
                if let value {
                    Circle()
                        .trim(from: 0, to: min(max(value / 100, 0), 1))
                        .stroke(color, style: StrokeStyle(lineWidth: 7, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .shadow(color: color.opacity(0.7), radius: 4)
                        .animation(.easeOut(duration: 0.4), value: value)
                }
                Text(verbatim: text).font(.caption.weight(.bold)).monospacedDigit().foregroundStyle(Theme.textPrimary)
            }
            .frame(width: 66, height: 66)
            Text(verbatim: title).font(.caption2.weight(.semibold)).foregroundStyle(Theme.textMuted)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: "\(title): \(text)"))
    }
}
