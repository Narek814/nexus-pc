import XCTest
@testable import NexusKit

final class LanDiscoveryTests: XCTestCase {
    func testNearestAddressesFirstAndKnownSkipped() {
        let list = LanDiscovery.candidates(near: ["192.168.0.102"])
        XCTAssertEqual(Array(list.prefix(4)), ["192.168.0.101", "192.168.0.103", "192.168.0.100", "192.168.0.104"])
        XCTAssertEqual(list.count, 253)
        XCTAssertFalse(list.contains("192.168.0.102"))
        XCTAssertFalse(list.contains("192.168.0.0"))
        XCTAssertFalse(list.contains("192.168.0.255"))
    }

    func testOnlyPrivateIPv4Networks() {
        XCTAssertTrue(LanDiscovery.candidates(near: ["100.101.1.2", "8.8.8.8", "fe80::1", "pc.local"]).isEmpty)
        XCTAssertEqual(LanDiscovery.candidates(near: ["10.0.5.7"]).first, "10.0.5.6")
        XCTAssertEqual(LanDiscovery.candidates(near: ["172.20.1.1"]).first, "172.20.1.2")
        XCTAssertTrue(LanDiscovery.candidates(near: ["172.32.1.1"]).isEmpty)
    }

    func testSeveralKnownNetworksAreMergedWithoutRepeats() {
        let list = LanDiscovery.candidates(near: ["192.168.0.10", "192.168.0.20", "10.0.0.1"])
        XCTAssertEqual(Set(list).count, list.count)
        XCTAssertFalse(list.contains("192.168.0.20"))
        XCTAssertTrue(list.contains("10.0.0.2"))
    }
}
