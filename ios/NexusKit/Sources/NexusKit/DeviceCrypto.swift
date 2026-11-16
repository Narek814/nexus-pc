import CryptoKit
import Foundation

/// Byte-exact counterpart of C# `Nexus.Core.Security.DeviceCrypto` (docs/API.md, "Сопряжение" and "Вход").
public enum DeviceCrypto {
    /// 6-digit code shown on both the phone and the PC during pairing, so the person can check that
    /// the PC is approving this phone. SHA-256("nexuspc-sas-v1\n" ‖ publicKey ‖ FINGERPRINT), first
    /// 4 bytes big-endian, modulo 1 000 000.
    public static func verificationCode(publicKey spki: Data, fingerprint: String) -> String {
        var data = Data("nexuspc-sas-v1\n".utf8)
        data.append(spki)
        data.append(Data(fingerprint.uppercased().utf8))
        let digest = Array(SHA256.hash(data: data))
        let value = (UInt32(digest[0]) << 24) | (UInt32(digest[1]) << 16) | (UInt32(digest[2]) << 8) | UInt32(digest[3])
        let code = String(value % 1_000_000)
        return String(repeating: "0", count: 6 - code.count) + code
    }

    /// The bytes a device signs for auth.prove. UUIDs are lowercase (C# "D" format), the fingerprint uppercase.
    public static func authTranscript(pcId: UUID, deviceId: UUID, nonce: Data, fingerprint: String) -> Data {
        let text = "nexuspc-auth-v1\n\(pcId.uuidString.lowercased())\n\(deviceId.uuidString.lowercased())\n"
            + "\(nonce.base64EncodedString())\n\(fingerprint.uppercased())"
        return Data(text.utf8)
    }

    /// SHA-256 of a DER certificate as uppercase hex: what the pairing link's `fp` must equal.
    public static func certificateFingerprint(der: Data) -> String {
        SHA256.hash(data: der).map { String(format: "%02X", $0) }.joined()
    }
}
