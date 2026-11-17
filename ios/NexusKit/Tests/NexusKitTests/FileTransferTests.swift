import XCTest
@testable import NexusKit

final class FileTransferTests: XCTestCase {
    private func temp(_ data: Data) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try data.write(to: url)
        return url
    }

    func testSha256MatchesKnownValues() throws {
        XCTAssertEqual(try FileDigest.sha256Hex(of: temp(Data())), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
        XCTAssertEqual(try FileDigest.sha256Hex(of: temp(Data("abc".utf8))), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
    }

    func testLargeFileHashesInPieces() throws {
        let data = Data((0..<3_000_000).map { UInt8($0 % 251) })
        let url = try temp(data)
        var whole = try FileDigest.sha256Hex(of: url)
        XCTAssertEqual(whole.count, 64)
        whole = try FileDigest.sha256Hex(of: try temp(data))
        XCTAssertEqual(whole, try FileDigest.sha256Hex(of: url))
    }

    func testChunksCoverTheFileExactly() throws {
        let data = Data((0..<1_200_000).map { UInt8($0 % 199) })
        let url = try temp(data)
        var rebuilt = Data()
        var offset: Int64 = 0
        while true {
            let chunk = try FileDigest.chunk(of: url, at: offset)
            if chunk.isEmpty { break }
            XCTAssertLessThanOrEqual(chunk.count, FileTransferLimits.chunkSize)
            rebuilt.append(chunk)
            offset += Int64(chunk.count)
        }
        XCTAssertEqual(rebuilt, data)
    }
}
