import XCTest
@testable import NexusKit

/// docs/design/ios-screens.md, section 2, steps 1–6.
final class SectionAccessTests: XCTestCase {
    private let caps: Set<String> = ["system.hello", "automations.list", "entitlements.v1"]
    private let proHello = AgentHello(result: ["capabilities": ["entitlements.v1"], "authenticated": true,
                                               "plan": "pro", "features": ["automations"], "entitlementState": "active"])
    private let freeHello = AgentHello(result: ["capabilities": ["entitlements.v1"], "authenticated": true,
                                                "plan": "free", "features": [], "entitlementState": "expired"])
    private let oldHello = AgentHello(result: ["capabilities": ["system.hello"], "authenticated": true])

    private func resolve(paired: Bool = true, connection: SectionAccess.Connection = .online, capabilities: Set<String>? = nil,
                         commands: [String] = ["automations.list"], session: Bool = true, needsTray: Bool = false,
                         hello: AgentHello? = nil, feature: String? = nil) -> SectionAccess {
        SectionAccess.resolve(isPaired: paired, connection: connection, capabilities: capabilities ?? caps,
                              anyOf: commands, userSessionAvailable: session, needsTray: needsTray,
                              hello: hello, requiredFeature: feature)
    }

    func testNoPCFirst() {
        XCTAssertEqual(resolve(paired: false, connection: .offline, capabilities: []), .noPC)
    }

    func testOfflineWithCachedCapabilities() {
        XCTAssertEqual(resolve(connection: .offline), .offline)
        XCTAssertEqual(resolve(connection: .offline, capabilities: []), .offline)
    }

    func testConnecting() {
        XCTAssertEqual(resolve(connection: .connecting), .connecting)
        XCTAssertEqual(resolve(connection: .connecting, capabilities: []), .connecting)
    }

    func testPermission() {
        XCTAssertEqual(resolve(commands: ["rgb.status"]), .permission)
        XCTAssertEqual(resolve(commands: ["rgb.status", "automations.list"]), .content)
    }

    func testPermissionBeforeTrayAndPro() {
        XCTAssertEqual(resolve(commands: ["rgb.status"], session: false, needsTray: true, hello: freeHello, feature: "automations"),
                       .permission)
    }

    func testTrayOnlyWhenNeeded() {
        XCTAssertEqual(resolve(session: false), .content)
        XCTAssertEqual(resolve(session: false, needsTray: true), .trayMissing)
        XCTAssertEqual(resolve(session: true, needsTray: true), .content)
    }

    func testTrayBeforePro() {
        XCTAssertEqual(resolve(session: false, needsTray: true, hello: freeHello, feature: "automations"), .trayMissing)
    }

    func testPro() {
        XCTAssertEqual(resolve(hello: freeHello, feature: "automations"), .pro(.expired))
        XCTAssertEqual(resolve(hello: proHello, feature: "automations"), .content)
        XCTAssertEqual(resolve(hello: proHello, feature: "rgb.sync"), .pro(.active))
        XCTAssertEqual(resolve(hello: freeHello, feature: nil), .content)
    }

    func testNeverProWithoutMarker() {
        XCTAssertEqual(resolve(hello: oldHello, feature: "automations"), .content)
        XCTAssertEqual(resolve(hello: nil, feature: "automations"), .content)
    }
}
