import Foundation
import NexusKit

/// metrics.snapshot (docs/API.md «Мониторинг»). nil = the PC could not read it; `unavailable` says why.
struct MetricsReading: Equatable {
    struct Gpu: Equatable, Identifiable {
        var id: String { name }
        let name: String
        let usage: Double?
        let temperature: Double?
        let memoryUsed: Double?
        let memoryTotal: Double?
        let coreClock: Double?
    }

    struct Disk: Equatable, Identifiable {
        var id: String { name }
        let name: String
        let total: Double
        let free: Double
        let usage: Double
    }

    let cpuUsage: Double?
    let cpuTemperature: Double?
    let cpuClock: Double?
    let logicalProcessors: Int?
    let memoryUsage: Double?
    let memoryUsed: Double?
    let memoryTotal: Double?
    let gpus: [Gpu]
    let disks: [Disk]
    let receivePerSecond: Double?
    let sendPerSecond: Double?
    /// PC text per metric: fallback only.
    let unavailable: [String: String]
    /// Stable reason code per metric (error-texts.md, 3.8); older agents send none.
    let unavailableCodes: [String: String]

    init(_ p: [String: Any]) {
        let cpu = p["cpu"] as? [String: Any] ?? [:]
        let memory = p["memory"] as? [String: Any] ?? [:]
        let network = p["network"] as? [String: Any] ?? [:]
        cpuUsage = Self.number(cpu["usage"])
        cpuTemperature = Self.number(cpu["temperature"])
        cpuClock = Self.number(cpu["clockMhz"])
        logicalProcessors = cpu["logicalProcessors"] as? Int
        memoryUsage = Self.number(memory["usage"])
        memoryUsed = Self.number(memory["usedBytes"])
        memoryTotal = Self.number(memory["totalBytes"])
        gpus = (p["gpus"] as? [[String: Any]] ?? []).map {
            Gpu(name: $0["name"] as? String ?? "GPU", usage: Self.number($0["usage"]), temperature: Self.number($0["temperature"]),
                memoryUsed: Self.number($0["memoryUsedBytes"]), memoryTotal: Self.number($0["memoryTotalBytes"]),
                coreClock: Self.number($0["coreClockMhz"]))
        }
        disks = (p["disks"] as? [[String: Any]] ?? []).compactMap {
            guard let name = $0["name"] as? String, let total = Self.number($0["totalBytes"]), let free = Self.number($0["freeBytes"]) else { return nil }
            return Disk(name: name, total: total, free: free, usage: Self.number($0["usage"]) ?? 0)
        }
        receivePerSecond = Self.number(network["receiveBytesPerSecond"])
        sendPerSecond = Self.number(network["sendBytesPerSecond"])
        unavailable = ErrorPresentation.reasonMap(p["unavailable"])
        unavailableCodes = ErrorPresentation.reasonMap(p["unavailableCodes"])
    }

    /// Why `metric` (a key of `unavailable`, e.g. `cpu.temperature`) has no value: by code, then PC text.
    func reason(for metric: String) -> ErrorPresentation.ReasonText? {
        guard unavailable[metric] != nil || unavailableCodes[metric] != nil else {
            return nil
        }
        return ErrorPresentation.metricReason(code: unavailableCodes[metric], pcText: unavailable[metric])
    }

    /// Every metric with a reason, sorted.
    var unavailableMetrics: [String] {
        Set(unavailable.keys).union(unavailableCodes.keys).sorted()
    }

    static func number(_ value: Any?) -> Double? {
        (value as? NSNumber)?.doubleValue
    }
}

struct MetricsPoint: Identifiable, Equatable {
    var id: Date { time }
    let time: Date
    let cpu: Double?
    let memory: Double?
    let gpu: Double?
    let gpuTemperature: Double?
    let receive: Double?
    let send: Double?

    static func parse(_ payload: [String: Any]) -> [MetricsPoint] {
        (payload["points"] as? [[String: Any]] ?? []).compactMap { p in
            guard let text = p["t"] as? String, let time = parseDate(text) else { return nil }
            return MetricsPoint(time: time, cpu: MetricsReading.number(p["cpu"]), memory: MetricsReading.number(p["memory"]),
                                gpu: MetricsReading.number(p["gpu"]), gpuTemperature: MetricsReading.number(p["gpuTemperature"]),
                                receive: MetricsReading.number(p["rx"]), send: MetricsReading.number(p["tx"]))
        }
    }

    /// The agent writes up to 7 fractional digits (.NET "O"); ISO8601DateFormatter wants at most 3.
    static func parseDate(_ text: String) -> Date? {
        var value = text
        if let dot = value.firstIndex(of: "."), let end = value[dot...].firstIndex(where: { $0 == "Z" || $0 == "+" || $0 == "-" }) {
            let fraction = value[value.index(after: dot)..<end]
            value.replaceSubrange(value.index(after: dot)..<end, with: String(fraction.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0))
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) {
            return date
        }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }
}

enum Format {
    static func percent(_ value: Double?) -> String {
        value.map { "\(Int($0.rounded()))%" } ?? "—"
    }

    static func temperature(_ value: Double?) -> String {
        value.map { "\(Int($0.rounded()))°C" } ?? "—"
    }

    static func bytes(_ value: Double?) -> String {
        guard let value else { return "—" }
        return ByteCountFormatter.string(fromByteCount: Int64(value), countStyle: .memory)
    }

    static func rate(_ value: Double?) -> String {
        guard let value else { return "—" }
        return String(localized: "monitor.rate \(ByteCountFormatter.string(fromByteCount: Int64(value), countStyle: .decimal))")
    }
}
