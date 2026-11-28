import XCTest
@testable import NexusKit

final class RgbStatusTests: XCTestCase {
    private let conflictJSON = #"{"available":false,"state":"conflict","reason":"Подсветка недоступна: её перехватила другая программа на ПК.","devices":[],"profiles":[]}"#
    private let legacyJSON = #"{"available":true,"devices":[],"profiles":[]}"#

    func testDecodesConflictState() throws {
        let status = try JSONDecoder().decode(RgbStatus.self, from: Data(conflictJSON.utf8))
        XCTAssertFalse(status.available)
        XCTAssertEqual(status.state, "conflict")
        XCTAssertTrue(status.isConflict)
        XCTAssertNotNil(status.reason)
    }

    func testDecodesWithoutStateFromOldAgent() throws {
        let status = try JSONDecoder().decode(RgbStatus.self, from: Data(legacyJSON.utf8))
        XCTAssertTrue(status.available)
        XCTAssertNil(status.state)
        XCTAssertFalse(status.isConflict)
    }

    func testDictionaryInitMatchesDecoder() throws {
        for json in [conflictJSON, legacyJSON] {
            let dict = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
            XCTAssertEqual(RgbStatus(result: dict), try JSONDecoder().decode(RgbStatus.self, from: Data(json.utf8)))
        }
    }

    func testReasonCodeFromFixtures() throws {
        for (name, code) in [("rgb.status unavailable", "openrgbNotResponding"), ("rgb.status conflict", "conflict")] {
            let c = try XCTUnwrap(try AgentHelloTests.cases().first { $0.name == name }, name)
            let payload = try XCTUnwrap(c.message["payload"] as? [String: Any], name)
            let status = RgbStatus(result: payload)
            XCTAssertFalse(status.available, name)
            XCTAssertEqual(status.reasonCode, code, name)
            let data = try JSONSerialization.data(withJSONObject: payload)
            XCTAssertEqual(try JSONDecoder().decode(RgbStatus.self, from: data), status, name)
        }
    }

    func testMissingReasonCodeIsNil() throws {
        XCTAssertNil(RgbStatus(result: ["available": false, "reason": "x"]).reasonCode)
        XCTAssertNil(try JSONDecoder().decode(RgbStatus.self, from: Data(conflictJSON.utf8)).reasonCode)
        XCTAssertNil(RgbStatus(available: true).reasonCode)
    }

    func testUnknownStateIsNotConflict() {
        XCTAssertFalse(RgbStatus(result: ["available": false, "state": "other"]).isConflict)
    }
}
