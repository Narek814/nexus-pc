import Foundation
import XCTest
@testable import NexusKit

/// End-to-end against a real Nexus.Agent started by CI (.github/workflows/ios.yml) on this machine:
/// the CI runs `Nexus.Agent pair` with "y" on stdin and writes its output to NEXUS_TEST_PAIR_OUTPUT.
/// Skipped when that variable is not set (e.g. local runs).
final class LiveAgentTests: XCTestCase {
    private func pairingLink() async throws -> PairingLink {
        guard let path = ProcessInfo.processInfo.environment["NEXUS_TEST_PAIR_OUTPUT"] else {
            throw XCTSkip("NEXUS_TEST_PAIR_OUTPUT not set: no live agent")
        }
        for _ in 0..<60 {
            if let text = try? String(contentsOfFile: path, encoding: .utf8),
               let range = text.range(of: #"nexuspc://pair\?\S+"#, options: .regularExpression),
               let link = PairingLink(String(text[range])) {
                return link
            }
            try await Task.sleep(nanoseconds: 500_000_000)
        }
        XCTFail("No pairing link appeared in \(path)")
        throw NexusError.timeout
    }

    func testPairAuthenticateAndUseTheAgentLikeTheApp() async throws {
        let link = try await pairingLink()
        let signer = SoftwareSigner()

        // Pairing (the CI answered "y" on the PC side).
        let (pairing, hello) = try await NexusConnection.connect(host: "127.0.0.1", port: link.port, fingerprint: link.certificateFingerprint)
        XCTAssertEqual(hello["pcId"] as? String, link.pcId.uuidString.lowercased())
        XCTAssertEqual(hello["authenticated"] as? Bool, false)
        let (deviceId, permissions) = try await pairing.pair(code: link.code, deviceName: "NexusKit CI", signer: signer)
        XCTAssertEqual(Set(permissions), ["power", "apps", "monitoring", "rgb"])
        pairing.close()

        // A fresh connection, as the app does every time.
        let (connection, _) = try await NexusConnection.connect(host: "127.0.0.1", port: link.port, fingerprint: link.certificateFingerprint)
        defer { connection.close() }
        try await connection.authenticate(pcId: link.pcId, deviceId: deviceId, fingerprint: link.certificateFingerprint, signer: signer)

        let status = try await connection.request("pc.status").unwrap()
        XCTAssertEqual(status["pcId"] as? String, link.pcId.uuidString.lowercased())
        XCTAssertNotNil(status["uptimeSeconds"])

        // Concurrent requests are matched by id.
        async let a = connection.request("pc.status")
        async let b = connection.request("system.hello")
        let (ra, rb) = try await (a, b)
        XCTAssertNotNil(ra.payload["uptimeSeconds"])
        XCTAssertEqual(rb.payload["authenticated"] as? Bool, true)

        // Critical commands need confirmation; the CI agent is not on Windows, so power is unsupported.
        let unconfirmed = try await connection.request("power.shutdown", payload: ["delaySeconds": 600])
        XCTAssertEqual(unconfirmed.errorCode, "confirm.required")
        let sleep = try await connection.request("power.sleep")
        XCTAssertEqual(sleep.errorCode, "power.unsupported")
        // Monitoring: the CI agent is not on Windows, so CPU/memory are null with a reason, never invented.
        let metrics = try await connection.request("metrics.snapshot").unwrap()
        let cpu = try XCTUnwrap(metrics["cpu"] as? [String: Any])
        XCTAssertTrue(cpu["usage"] is NSNull || cpu["usage"] == nil)
        let unavailable = try XCTUnwrap(metrics["unavailable"] as? [String: Any])
        XCTAssertNotNil(unavailable["cpu.temperature"] as? String)
        let history = try await connection.request("metrics.history", payload: ["range": "10m"]).unwrap()
        XCTAssertEqual(history["intervalSeconds"] as? Int, 2)
        XCTAssertNotNil(history["points"] as? [Any])

        // Alerts: ntfy is off by default; enabling gives a random topic. (No delivery: CI must not post to ntfy.sh.)
        let alerts = try await connection.request("alerts.get").unwrap()
        XCTAssertEqual((alerts["ntfy"] as? [String: Any])?["enabled"] as? Bool, false)
        let badThreshold = try await connection.request("alerts.set", payload: ["gpuTemperatureC": 300])
        XCTAssertEqual(badThreshold.errorCode, "command.invalidPayload")

        // No tray on the CI machine.
        let apps = try await connection.request("apps.list")
        XCTAssertEqual(apps.errorCode, "session.unavailable")
    }

    func testWrongCertificatePinIsRefused() async throws {
        let link = try await pairingLink()
        do {
            _ = try await NexusConnection.connect(host: "127.0.0.1", port: link.port, fingerprint: String(repeating: "0", count: 64), timeout: 5)
            XCTFail("Connected despite a wrong pin")
        } catch let error as NexusError {
            if case .server = error {
                XCTFail("Expected a connection failure, got \(error)")
            }
        }
    }

    func testWrongKeyCannotAuthenticate() async throws {
        let link = try await pairingLink()
        let (connection, _) = try await NexusConnection.connect(host: "127.0.0.1", port: link.port, fingerprint: link.certificateFingerprint)
        defer { connection.close() }
        do {
            try await connection.authenticate(pcId: link.pcId, deviceId: UUID(), fingerprint: link.certificateFingerprint, signer: SoftwareSigner())
            XCTFail("Authenticated with an unknown device")
        } catch let error as NexusError {
            XCTAssertEqual(error.code, "auth.failed")
        }
    }
}
