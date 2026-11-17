import Foundation

/// H.264 Annex-B helpers (the PC sends access units with start codes; Apple's decoder wants
/// length-prefixed AVCC units plus SPS/PPS in a format description).
public enum H264Stream {
    public static let sps = 7
    public static let pps = 8
    public static let idr = 5
    public static let slice = 1

    /// NAL units without their start codes.
    public static func nalUnits(_ data: Data) -> [Data] {
        let bytes = [UInt8](data)
        var starts: [(Int, Int)] = [] // (start code index, payload index)
        var i = 0
        while i + 2 < bytes.count {
            if bytes[i] == 0, bytes[i + 1] == 0, bytes[i + 2] == 1 {
                let codeStart = (i > 0 && bytes[i - 1] == 0) ? i - 1 : i
                starts.append((codeStart, i + 3))
                i += 3
            } else {
                i += 1
            }
        }
        var units: [Data] = []
        for (index, start) in starts.enumerated() {
            let end = index + 1 < starts.count ? starts[index + 1].0 : bytes.count
            if start.1 < end {
                units.append(Data(bytes[start.1..<end]))
            }
        }
        return units
    }

    public static func type(of nal: Data) -> Int {
        guard let first = nal.first else { return -1 }
        return Int(first & 0x1F)
    }

    /// Picture NAL units (slices) as one AVCC block: each prefixed with its 4-byte big-endian length.
    public static func avcc(_ units: [Data]) -> Data {
        var out = Data()
        for unit in units {
            var length = UInt32(unit.count).bigEndian
            withUnsafeBytes(of: &length) { out.append(contentsOf: $0) }
            out.append(unit)
        }
        return out
    }
}
