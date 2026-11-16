import Foundation

public enum NexusProtocol {
    public static let version = 1
}

/// Builds request envelopes (shared/schemas/request.v1.schema.json).
public enum RequestEnvelope {
    public static func make(
        type: String,
        payload: [String: Any] = [:],
        confirmed: Bool = false,
        id: UUID = UUID(),
        now: Date = Date()
    ) throws -> Data {
        var envelope: [String: Any] = [
            "v": NexusProtocol.version,
            "id": id.uuidString.lowercased(),
            "kind": "request",
            "type": type,
            "ts": timestamp(now),
            "payload": payload,
        ]
        if confirmed {
            envelope["confirmed"] = true
        }
        return try JSONSerialization.data(withJSONObject: envelope, options: [.sortedKeys])
    }

    /// RFC 3339 in UTC with milliseconds, e.g. 2026-11-16T13:38:11.123Z.
    public static func timestamp(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        return formatter.string(from: date)
    }
}
