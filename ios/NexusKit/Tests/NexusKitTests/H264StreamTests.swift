import XCTest
@testable import NexusKit

final class H264StreamTests: XCTestCase {
    func testSplitsThreeAndFourByteStartCodes() {
        let stream = Data([0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 4, 5])
        let units = H264Stream.nalUnits(stream)
        XCTAssertEqual(units.map { H264Stream.type(of: $0) }, [7, 8, 5])
        XCTAssertEqual(units[0], Data([0x67, 1, 2]))
        XCTAssertEqual(units[1], Data([0x68, 3]))
        XCTAssertEqual(units[2], Data([0x65, 4, 5]))
    }

    func testAvccPrefixesBigEndianLengths() {
        let block = H264Stream.avcc([Data([0x65, 1, 2]), Data([0x41])])
        XCTAssertEqual(block, Data([0, 0, 0, 3, 0x65, 1, 2, 0, 0, 0, 1, 0x41]))
    }

    func testEmptyOrGarbageGivesNothing() {
        XCTAssertTrue(H264Stream.nalUnits(Data()).isEmpty)
        XCTAssertTrue(H264Stream.nalUnits(Data([1, 2, 3, 4])).isEmpty)
    }
}
