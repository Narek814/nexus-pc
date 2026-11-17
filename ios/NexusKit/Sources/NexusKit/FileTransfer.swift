import CryptoKit
import Foundation

/// Shared rules for file transfer (docs/API.md "Файлы").
public enum FileTransferLimits {
    /// Bytes per files.upload.chunk / files.read (the agent's limit).
    public static let chunkSize = 512 * 1024
}

public enum FileDigest {
    /// Lowercase hex SHA-256 of a file, read in 1 MiB pieces (videos do not fit in memory).
    public static func sha256Hex(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let data = try handle.read(upToCount: 1 << 20), !data.isEmpty {
            hasher.update(data: data)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// One chunk of a file at an offset, for resumable uploads.
    public static func chunk(of url: URL, at offset: Int64, size: Int = FileTransferLimits.chunkSize) throws -> Data {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        try handle.seek(toOffset: UInt64(offset))
        return try handle.read(upToCount: size) ?? Data()
    }
}
