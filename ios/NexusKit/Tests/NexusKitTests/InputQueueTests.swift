import XCTest
@testable import NexusKit

final class InputQueueTests: XCTestCase {
    private func drain(_ queue: inout InputQueue) -> [String] {
        var out: [String] = []
        while let next = queue.next() {
            let keys = next.payload.keys.sorted().map { "\($0)=\(next.payload[$0]!)" }.joined(separator: ",")
            out.append("\(next.type) \(keys)")
        }
        return out
    }

    func testMovesMergeButNeverAcrossAClick() {
        var q = InputQueue()
        q.send(.button("left", action: "down"))
        q.move(dx: 3, dy: 1)
        q.move(dx: 4, dy: 1)
        q.send(.button("left", action: "up"))
        q.move(dx: -2, dy: 0)
        XCTAssertEqual(drain(&q), [
            "input.button action=down,button=left",
            "input.move dx=7,dy=2",
            "input.button action=up,button=left",
            "input.move dx=-2,dy=0",
        ])
    }

    func testFractionsCarryOver() {
        var q = InputQueue()
        q.move(dx: 0.6, dy: -0.6)
        XCTAssertNil(q.next())
        q.move(dx: 0.6, dy: -0.6)
        XCTAssertEqual(drain(&q), ["input.move dx=1,dy=-1"])
    }

    func testHugeMovesAreClampedNotReplayed() {
        var q = InputQueue()
        q.move(dx: 9000, dy: 0)
        XCTAssertEqual(drain(&q), ["input.move dx=2000,dy=0"])
        q.move(dx: 0.5, dy: 0)
        XCTAssertNil(q.next())
    }

    func testScrollSendsOnlyNonZeroAxes() {
        var q = InputQueue()
        q.scroll(dx: 0, dy: -100)
        q.scroll(dx: 0, dy: -50)
        XCTAssertEqual(drain(&q), ["input.scroll dy=-150"])
    }

    func testLongTextIsSplitIntoAllowedChunks() {
        var q = InputQueue()
        q.send(.text(String(repeating: "я", count: 300)))
        let first = q.next()!
        XCTAssertEqual((first.payload["text"] as! String).count, 256)
        XCTAssertEqual((q.next()!.payload["text"] as! String).count, 44)
        XCTAssertNil(q.next())
    }

    func testKeyPayloadOmitsEmptyModifiers() {
        XCTAssertEqual(InputCommand.key("enter").payload.keys.sorted(), ["key"])
        XCTAssertEqual(InputCommand.key("c", modifiers: ["ctrl"]).payload["modifiers"] as? [String], ["ctrl"])
    }

    func testClearDropsEverything() {
        var q = InputQueue()
        q.move(dx: 5, dy: 5)
        q.send(.media("mute"))
        q.clear()
        XCTAssertTrue(q.isEmpty)
        XCTAssertNil(q.next())
    }

    func testAbsoluteMovesKeepOnlyTheLatestButNeverJumpAClick() {
        var q = InputQueue()
        q.send(.moveTo(x: 10, y: 10))
        q.send(.moveTo(x: 20, y: 20))
        q.send(.button("left", action: "down"))
        q.send(.moveTo(x: 30, y: 30))
        q.send(.moveTo(x: 99999, y: -5))
        q.send(.button("left", action: "up"))
        XCTAssertEqual(drain(&q), [
            "input.moveTo x=20,y=20",
            "input.button action=down,button=left",
            "input.moveTo x=65535,y=0",
            "input.button action=up,button=left",
        ])
    }
}
