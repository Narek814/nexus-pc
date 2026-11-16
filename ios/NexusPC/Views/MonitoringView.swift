import Charts
import SwiftUI

/// Monitoring (P4a): live gauges, history charts, disks, network. Values the PC cannot read are shown
/// as "—" with the reason, never invented.
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

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if let error {
                        Label(error, systemImage: "exclamationmark.triangle").font(.footnote).foregroundStyle(.orange)
                    }
                    gauges
                    SectionTitle("История")
                    chartCard
                    if let gpus = reading?.gpus, !gpus.isEmpty {
                        SectionTitle("Видеокарта")
                        ForEach(gpus) { gpuCard($0) }
                    }
                    SectionTitle("Диски")
                    disksCard
                    SectionTitle("Сеть")
                    networkCard
                    unavailableCard
                    settingsCard
                }
                .padding(20)
            }
        }
        .navigationTitle("Мониторинг")
        .navigationBarTitleDisplayMode(.inline)
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

    private var chartCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 12) {
                Picker("Период", selection: $range) {
                    Text("10 мин").tag("10m")
                    Text("1 час").tag("1h")
                    Text("24 часа").tag("24h")
                }
                .pickerStyle(.segmented)

                if points.isEmpty {
                    Text(range == "10m" ? "Данные появляются каждые 2 секунды." : "Средние значения за минуту появятся через минуту работы службы.")
                        .font(.footnote)
                        .foregroundStyle(Theme.textMuted)
                        .frame(height: 160)
                } else {
                    Chart {
                        ForEach(points) { p in
                            if showCpu, let v = p.cpu {
                                LineMark(x: .value("Время", p.time), y: .value("%", v), series: .value("Показатель", "CPU"))
                                    .foregroundStyle(by: .value("Показатель", "CPU"))
                            }
                            if showGpu, let v = p.gpu {
                                LineMark(x: .value("Время", p.time), y: .value("%", v), series: .value("Показатель", "GPU"))
                                    .foregroundStyle(by: .value("Показатель", "GPU"))
                            }
                            if showMemory, let v = p.memory {
                                LineMark(x: .value("Время", p.time), y: .value("%", v), series: .value("Показатель", "RAM"))
                                    .foregroundStyle(by: .value("Показатель", "RAM"))
                            }
                            if showGpuTemp, let v = p.gpuTemperature {
                                LineMark(x: .value("Время", p.time), y: .value("%", v), series: .value("Показатель", "GPU °C"))
                                    .foregroundStyle(by: .value("Показатель", "GPU °C"))
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

    private func gpuCard(_ gpu: MetricsReading.Gpu) -> some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 6) {
                Text(gpu.name).font(.headline).foregroundStyle(Theme.textPrimary)
                row("Загрузка", Format.percent(gpu.usage))
                row("Температура", Format.temperature(gpu.temperature))
                row("Видеопамять", "\(Format.bytes(gpu.memoryUsed)) из \(Format.bytes(gpu.memoryTotal))")
                row("Частота ядра", gpu.coreClock.map { "\(Int($0)) МГц" } ?? "—")
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
                                Text(disk.name).foregroundStyle(Theme.textPrimary)
                                Spacer()
                                Text("свободно \(Format.bytes(disk.free)) из \(Format.bytes(disk.total))")
                                    .font(.caption)
                                    .foregroundStyle(disk.free / max(disk.total, 1) < 0.1 ? .orange : Theme.textMuted)
                            }
                            ProgressView(value: min(max(disk.usage / 100, 0), 1)).tint(disk.usage > 90 ? .orange : Theme.cyan)
                        }
                    }
                } else {
                    Text("—").foregroundStyle(Theme.textMuted)
                }
            }
        }
    }

    private var networkCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 6) {
                row("Загрузка ↓", Format.rate(reading?.receivePerSecond))
                row("Отдача ↑", Format.rate(reading?.sendPerSecond))
                row("Память", "\(Format.bytes(reading?.memoryUsed)) из \(Format.bytes(reading?.memoryTotal))")
            }
        }
    }

    @ViewBuilder
    private var unavailableCard: some View {
        if let unavailable = reading?.unavailable, !unavailable.isEmpty {
            SectionTitle("Недоступно")
            GlassCard {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(unavailable.keys.sorted(), id: \.self) { key in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(Self.title(for: key)).font(.subheadline.weight(.semibold)).foregroundStyle(Theme.textPrimary)
                            Text(unavailable[key] ?? "").font(.caption).foregroundStyle(Theme.textMuted)
                        }
                    }
                }
            }
        }
    }

    private var settingsCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle("Настройки")
            GlassCard {
                VStack(alignment: .leading, spacing: 10) {
                    Picker("Обновлять каждые", selection: $interval) {
                        Text("1 с").tag(1.0)
                        Text("2 с").tag(2.0)
                        Text("5 с").tag(5.0)
                    }
                    .pickerStyle(.segmented)
                    Toggle("CPU на графике", isOn: $showCpu)
                    Toggle("GPU на графике", isOn: $showGpu)
                    Toggle("RAM на графике", isOn: $showMemory)
                    Toggle("Температура GPU на графике", isOn: $showGpuTemp)
                }
                .tint(Theme.violet)
                .foregroundStyle(Theme.textPrimary)
            }
        }
    }

    private func row(_ title: String, _ value: String) -> some View {
        HStack {
            Text(title).foregroundStyle(Theme.textMuted)
            Spacer()
            Text(value).monospacedDigit().foregroundStyle(Theme.textPrimary)
        }
        .font(.subheadline)
    }

    // MARK: - Data

    private func poll() async {
        do {
            reading = MetricsReading(try await controller.fetch("metrics.snapshot"))
            error = nil
        } catch {
            self.error = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    private func loadHistory() async {
        if let payload = try? await controller.fetch("metrics.history", payload: ["range": range]) {
            points = MetricsPoint.parse(payload)
        }
    }

    private static func title(for key: String) -> String {
        switch key {
        case "cpu.temperature": return "Температура процессора"
        case "cpu.clock": return "Частота процессора"
        case "cpu.usage": return "Загрузка процессора"
        case "memory": return "Память"
        case "gpu": return "Видеокарта"
        default: return key
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
                Text(text).font(.caption.weight(.bold)).monospacedDigit().foregroundStyle(Theme.textPrimary)
            }
            .frame(width: 66, height: 66)
            Text(title).font(.caption2.weight(.semibold)).foregroundStyle(Theme.textMuted)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(title): \(text)")
    }
}
