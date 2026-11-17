import Foundation

/// A discrete touchpad/keyboard action (docs/API.md "Тачпад").
public enum InputCommand: Equatable, Sendable {
    case button(_ button: String, action: String)
    /// Absolute position on the PC's primary monitor, 0...65535 on both axes (remote screen).
    case moveTo(x: Int, y: Int)
    case key(_ key: String, modifiers: [String] = [])
    case text(String)
    case media(String)

    public var type: String {
        switch self {
        case .button: "input.button"
        case .moveTo: "input.moveTo"
        case .key: "input.key"
        case .text: "input.text"
        case .media: "input.media"
        }
    }

    public var payload: [String: Any] {
        switch self {
        case let .button(button, action): ["button": button, "action": action]
        case let .moveTo(x, y): ["x": max(0, min(65535, x)), "y": max(0, min(65535, y))]
        case let .key(key, modifiers): modifiers.isEmpty ? ["key": key] : ["key": key, "modifiers": modifiers]
        case let .text(text): ["text": text]
        case let .media(action): ["action": action]
        }
    }
}

/// Orders and coalesces touchpad input so it can be sent one request at a time:
/// consecutive moves (and scrolls) merge while a request is in flight, but never across a click
/// or key press, so a drag stays down → moves → up. Fractions of a pixel carry over to the next send.
public struct InputQueue: Sendable {
    public static let maxMove = 2000
    public static let maxScroll = 2400
    public static let maxText = 256

    private enum Item: Equatable {
        case move(Double, Double)
        case scroll(Double, Double)
        case command(InputCommand)
    }

    private var items: [Item] = []
    private var moveCarry = (x: 0.0, y: 0.0)
    private var scrollCarry = (x: 0.0, y: 0.0)

    public init() {}

    public var isEmpty: Bool { items.isEmpty }

    public mutating func move(dx: Double, dy: Double) {
        if case let .move(x, y)? = items.last {
            items[items.count - 1] = .move(x + dx, y + dy)
        } else {
            items.append(.move(dx, dy))
        }
    }

    /// Wheel units: 120 = one notch; positive dy scrolls up.
    public mutating func scroll(dx: Double, dy: Double) {
        if case let .scroll(x, y)? = items.last {
            items[items.count - 1] = .scroll(x + dx, y + dy)
        } else {
            items.append(.scroll(dx, dy))
        }
    }

    public mutating func send(_ command: InputCommand) {
        if case let .text(text) = command {
            // The agent accepts at most 256 characters per request: split longer pastes.
            var rest = Substring(text)
            while !rest.isEmpty {
                var chunk = rest.prefix(Self.maxText)
                while chunk.utf16.count > Self.maxText { chunk = chunk.dropLast() }
                items.append(.command(.text(String(chunk))))
                rest = rest.dropFirst(chunk.count)
            }
            return
        }
        // Only the latest absolute position matters: consecutive ones replace each other.
        if case .moveTo = command, case .command(.moveTo)? = items.last {
            items[items.count - 1] = .command(command)
            return
        }
        items.append(.command(command))
    }

    public mutating func clear() {
        items.removeAll()
        moveCarry = (0, 0)
        scrollCarry = (0, 0)
    }

    /// The next request to send, or nil. Moves smaller than a pixel are kept for later.
    public mutating func next() -> (type: String, payload: [String: Any])? {
        while !items.isEmpty {
            switch items.removeFirst() {
            case let .command(command):
                return (command.type, command.payload)
            case let .move(dx, dy):
                let (x, y) = Self.take(dx, dy, carry: &moveCarry, limit: Self.maxMove)
                if x != 0 || y != 0 {
                    return ("input.move", ["dx": x, "dy": y])
                }
            case let .scroll(dx, dy):
                let (x, y) = Self.take(dx, dy, carry: &scrollCarry, limit: Self.maxScroll)
                if x != 0 || y != 0 {
                    var payload: [String: Any] = [:]
                    if x != 0 { payload["dx"] = x }
                    if y != 0 { payload["dy"] = y }
                    return ("input.scroll", payload)
                }
            }
        }
        return nil
    }

    private static func take(_ dx: Double, _ dy: Double, carry: inout (x: Double, y: Double), limit: Int) -> (Int, Int) {
        let totalX = dx + carry.x
        let totalY = dy + carry.y
        let x = max(-limit, min(limit, Int(totalX.rounded(.towardZero))))
        let y = max(-limit, min(limit, Int(totalY.rounded(.towardZero))))
        // Whatever exceeds the per-request limit is dropped rather than replayed later as a jump.
        carry = (
            abs(totalX) > Double(limit) ? 0 : totalX - Double(x),
            abs(totalY) > Double(limit) ? 0 : totalY - Double(y)
        )
        return (x, y)
    }
}
