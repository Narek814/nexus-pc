import CryptoKit
import Foundation
import XCTest
@testable import NexusKit

/// Runs the same shared/protocol/fixtures as the C# tests (windows/Nexus.Tests/CrossLanguageFixtureTests.cs).
final class SharedFixtureTests: XCTestCase {
    private static let fixtures: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent() // NexusKitTests
        .deletingLastPathComponent() // Tests
        .deletingLastPathComponent() // NexusKit
        .deletingLastPathComponent() // ios
        .deletingLastPathComponent() // repository root
        .appendingPathComponent("shared/protocol/fixtures")

    private func load<T: Decodable>(_ name: String, as type: T.Type) throws -> T {
        try JSONDecoder().decode(T.self, from: Data(contentsOf: Self.fixtures.appendingPathComponent(name)))
    }

    private struct CryptoVectors: Decodable {
        struct Code: Decodable { let publicKey: String; let fingerprint: String; let code: String }
        struct Transcript: Decodable {
            let pcId: String; let deviceId: String; let nonce: String; let fingerprint: String
            let transcript: String; let publicKey: String; let signatureDer: String
        }
        let verificationCodes: [Code]
        let authTranscripts: [Transcript]
    }

    private struct LinkVectors: Decodable {
        struct Valid: Decodable {
            let link: String; let pcId: String; let pcName: String; let hosts: [String]
            let port: Int; let fingerprint: String; let code: String
        }
        struct Invalid: Decodable { let why: String; let link: String }
        let valid: [Valid]
        let invalid: [Invalid]
    }

    func testVerificationCodesMatchCSharp() throws {
        let vectors = try load("crypto.v1.json", as: CryptoVectors.self)
        XCTAssertFalse(vectors.verificationCodes.isEmpty)
        for v in vectors.verificationCodes {
            let key = try XCTUnwrap(Data(base64Encoded: v.publicKey))
            XCTAssertEqual(DeviceCrypto.verificationCode(publicKey: key, fingerprint: v.fingerprint), v.code)
        }
    }

    func testAuthTranscriptsAndDerSignaturesMatchCSharp() throws {
        let vectors = try load("crypto.v1.json", as: CryptoVectors.self)
        XCTAssertFalse(vectors.authTranscripts.isEmpty)
        for v in vectors.authTranscripts {
            let transcript = DeviceCrypto.authTranscript(
                pcId: try XCTUnwrap(UUID(uuidString: v.pcId)),
                deviceId: try XCTUnwrap(UUID(uuidString: v.deviceId)),
                nonce: try XCTUnwrap(Data(base64Encoded: v.nonce)),
                fingerprint: v.fingerprint)
            XCTAssertEqual(String(decoding: transcript, as: UTF8.self), v.transcript)

            // The agent expects DER signatures; CryptoKit must read the same format it will produce.
            let key = try P256.Signing.PublicKey(derRepresentation: try XCTUnwrap(Data(base64Encoded: v.publicKey)))
            let signature = try P256.Signing.ECDSASignature(derRepresentation: try XCTUnwrap(Data(base64Encoded: v.signatureDer)))
            XCTAssertTrue(key.isValidSignature(signature, for: transcript))
        }
    }

    func testPairingLinksMatchCSharp() throws {
        let vectors = try load("pairing-links.v1.json", as: LinkVectors.self)
        for v in vectors.valid {
            let link = try XCTUnwrap(PairingLink(v.link), v.link)
            XCTAssertEqual(link.pcId, UUID(uuidString: v.pcId))
            XCTAssertEqual(link.pcName, v.pcName)
            XCTAssertEqual(link.hosts, v.hosts)
            XCTAssertEqual(link.port, v.port)
            XCTAssertEqual(link.certificateFingerprint, v.fingerprint)
            XCTAssertEqual(link.code, v.code)
        }
        for v in vectors.invalid {
            XCTAssertNil(PairingLink(v.link), v.why)
        }
    }
}

final class DeviceKeyFormatTests: XCTestCase {
    /// What the app will send in pair.request: SubjectPublicKeyInfo DER of a P-256 key (91 bytes).
    func testPublicKeyIsSpkiDer() {
        let key = P256.Signing.PrivateKey()
        let spki = key.publicKey.derRepresentation
        XCTAssertEqual(spki.count, 91)
        XCTAssertEqual(Array(spki.prefix(2)), [0x30, 0x59]) // SEQUENCE, 89 bytes
    }

    /// Keys are the fixed test keys of shared/protocol/fixtures/crypto.v1.json; the expected values were
    /// produced by the agent's C# `Nexus.Core.Security.DeviceCrypto.DeviceKeyFingerprint` (T3).
    func testDeviceKeyFingerprintMatchesCSharp() throws {
        let vectors = [
            ("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBBsArFozQrIlVzJEfzWLidQhjKnGu7ggMkAcVV3BfBOMssi8oT021AyyhMtbd/qwLBVy3i61mgMWD8IV6BeWTA==",
             "EBE001F742CBA123"),
            ("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwvM5vwVswtosj1HP5qCdUX1PRMMyhbblcc9E4YyLgy0iou1ww+nsRW6kKYvJZzXWXUA7BFHXFvTkAcRdKYaHdg==",
             "A94C1A10709C20F0"),
        ]
        for (key, expected) in vectors {
            let spki = try XCTUnwrap(Data(base64Encoded: key))
            XCTAssertEqual(DeviceCrypto.deviceKeyFingerprint(publicKey: spki), expected)
        }
        XCTAssertEqual(DeviceCrypto.grouped("EBE001F742CBA123"), "EBE0 01F7 42CB A123")
    }

    /// A real signer's key gives the same 16-character form (what the iPhone shows next to the PC).
    func testDeviceKeyFingerprintOfSigner() {
        let signer = SoftwareSigner()
        let fingerprint = DeviceCrypto.deviceKeyFingerprint(publicKey: signer.publicKeySPKI)
        XCTAssertEqual(fingerprint.count, 16)
        XCTAssertEqual(fingerprint, fingerprint.uppercased())
        XCTAssertTrue(fingerprint.allSatisfy(\.isHexDigit))
    }

    func testSignatureRoundTripsAsDer() throws {
        let key = P256.Signing.PrivateKey()
        let data = Data("nexuspc".utf8)
        let der = try key.signature(for: data).derRepresentation
        XCTAssertLessThanOrEqual(der.count, 72)
        XCTAssertTrue(key.publicKey.isValidSignature(try P256.Signing.ECDSASignature(derRepresentation: der), for: data))
    }
}

final class RequestEnvelopeTests: XCTestCase {
    func testEnvelopeHasExactlyTheSchemaFields() throws {
        let id = UUID()
        let data = try RequestEnvelope.make(type: "power.shutdown", payload: ["delaySeconds": 600], confirmed: true, id: id,
                                            now: Date(timeIntervalSince1970: 1_763_300_291.123))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(Set(object.keys), ["v", "id", "kind", "type", "ts", "payload", "confirmed"])
        XCTAssertEqual(object["v"] as? Int, 1)
        XCTAssertEqual(object["id"] as? String, id.uuidString.lowercased())
        XCTAssertEqual(object["kind"] as? String, "request")
        XCTAssertEqual(object["confirmed"] as? Bool, true)
        let ts = try XCTUnwrap(object["ts"] as? String)
        XCTAssertNotNil(ts.range(of: #"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$"#,
                                 options: .regularExpression), ts)
    }

    func testConfirmedIsOmittedWhenFalse() throws {
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: try RequestEnvelope.make(type: "pc.status")) as? [String: Any])
        XCTAssertNil(object["confirmed"])
        XCTAssertEqual((object["payload"] as? [String: Any])?.isEmpty, true)
    }
}

final class WakeOnLanTests: XCTestCase {
    func testMagicPacketLayout() throws {
        let packet = try XCTUnwrap(WakeOnLan.magicPacket(mac: "AA:BB:CC:DD:EE:0F"))
        XCTAssertEqual(packet.count, 102)
        XCTAssertEqual(Array(packet.prefix(6)), [UInt8](repeating: 0xFF, count: 6))
        XCTAssertEqual(Array(packet[6..<12]), [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x0F])
        XCTAssertEqual(Array(packet.suffix(6)), [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x0F])
        XCTAssertNotNil(WakeOnLan.magicPacket(mac: "aa-bb-cc-dd-ee-0f"))
    }

    func testInvalidMacIsRejected() {
        XCTAssertNil(WakeOnLan.magicPacket(mac: "AA:BB:CC"))
        XCTAssertNil(WakeOnLan.magicPacket(mac: "GG:BB:CC:DD:EE:FF"))
    }

    func testOldPairingRecordsStillDecode() throws {
        let json = #"{"pcId":"42FE00F7-98AD-4607-84C0-4580B73D2F0E","name":"PC","hosts":["192.168.0.102"],"port":47800,"fingerprint":"AB","deviceId":"7BD2D25D-78E4-42F5-B261-3AC851D80599","permissions":["power"]}"#
        let pc = try JSONDecoder().decode(PairedPC.self, from: Data(json.utf8))
        XCTAssertNil(pc.wake)
    }
}
