import Foundation
import XCTest
@testable import NexusKit

/// AgentHello and error.details against shared/protocol/fixtures/responses.v1.json.
final class AgentHelloTests: XCTestCase {
    static let responsesURL: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent() // NexusKitTests
        .deletingLastPathComponent() // Tests
        .deletingLastPathComponent() // NexusKit
        .deletingLastPathComponent() // ios
        .deletingLastPathComponent() // repository root
        .appendingPathComponent("shared/protocol/fixtures/responses.v1.json")

    /// (name, valid, message) of every case.
    static func cases() throws -> [(name: String, valid: Bool, message: [String: Any])] {
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: responsesURL)) as? [String: Any])
        let list = try XCTUnwrap(root["cases"] as? [[String: Any]])
        return list.compactMap { item in
            guard let name = item["name"] as? String, let message = item["message"] as? [String: Any] else { return nil }
            return (name, item["valid"] as? Bool ?? false, message)
        }
    }

    private func helloPayloads() throws -> [(String, [String: Any])] {
        try Self.cases()
            .filter { $0.valid && $0.name.hasPrefix("system.hello") }
            .compactMap { c in (c.message["payload"] as? [String: Any]).map { (c.name, $0) } }
    }

    private func errorCase(_ name: String) throws -> [String: Any] {
        let c = try XCTUnwrap(try Self.cases().first { $0.name == name }, name)
        return try XCTUnwrap(c.message["error"] as? [String: Any], name)
    }

    func testParsesAllValidHelloFixtures() throws {
        let payloads = try helloPayloads()
        XCTAssertGreaterThanOrEqual(payloads.count, 4)
        for (name, payload) in payloads {
            let hello = AgentHello(result: payload)
            XCTAssertEqual(hello.protocolVersion, 1, name)
            XCTAssertEqual(hello.pcId, "86a77e46-afba-4ab0-8f08-68ff922e9cc2", name)
            XCTAssertEqual(hello.pcName, "GAMING-PC", name)
            XCTAssertEqual(hello.agentVersion, "0.2.0", name)
            XCTAssertTrue(hello.supportsEntitlements, name)
            XCTAssertTrue(hello.capabilities.contains("system.hello"), name)
        }
    }

    func testHelloBeforeAuthHasNoPlan() throws {
        let payload = try XCTUnwrap(try helloPayloads().first { $0.0 == "system.hello before auth.prove: no plan fields" }?.1)
        let hello = AgentHello(result: payload)
        XCTAssertFalse(hello.authenticated)
        XCTAssertNil(hello.plan)
        XCTAssertNil(hello.entitlementState)
        XCTAssertTrue(hello.features.isEmpty)
        XCTAssertNil(hello.validUntil)
    }

    func testHelloProActive() throws {
        let payload = try XCTUnwrap(try helloPayloads().first { $0.0 == "system.hello after auth.prove with plan (pro, active)" }?.1)
        let hello = AgentHello(result: payload)
        XCTAssertTrue(hello.authenticated)
        XCTAssertEqual(hello.plan, "pro")
        XCTAssertEqual(hello.features, ["automations", "files.unlimited", "rgb.sync"])
        XCTAssertEqual(hello.entitlementState, .active)
        XCTAssertEqual(hello.validUntil, ISO8601DateFormatter().date(from: "2026-11-23T12:00:00Z"))
    }

    func testHelloFreeAndGrace() throws {
        let payloads = try helloPayloads()
        let free = AgentHello(result: try XCTUnwrap(payloads.first { $0.0 == "system.hello after auth.prove, free without token" }?.1))
        XCTAssertEqual(free.plan, "free")
        XCTAssertEqual(free.entitlementState, EntitlementState.none)
        XCTAssertTrue(free.features.isEmpty)
        let grace = AgentHello(result: try XCTUnwrap(payloads.first { $0.0 == "system.hello after auth.prove, pro in grace" }?.1))
        XCTAssertEqual(grace.entitlementState, .grace)
        XCTAssertEqual(grace.features, ["automations"])
        XCTAssertNotNil(grace.validUntil)
    }

    func testExtraFieldsAndUnknownValuesDoNotBreakParsing() throws {
        var payload = try XCTUnwrap(try helloPayloads().first { $0.0 == "system.hello after auth.prove with plan (pro, active)" }?.1)
        payload["futureField"] = ["nested": true]
        payload["entitlementState"] = "trial"
        payload["plan"] = "enterprise"
        let hello = AgentHello(result: payload)
        XCTAssertEqual(hello.entitlementState, .unknown("trial"))
        XCTAssertEqual(hello.entitlementState?.rawValue, "trial")
        XCTAssertEqual(hello.plan, "enterprise")
        XCTAssertEqual(hello.pcName, "GAMING-PC")
        XCTAssertTrue(hello.supportsEntitlements)
    }

    func testOldAgentWithoutMarker() {
        let hello = AgentHello(result: ["protocol": 1, "pcId": "x", "pcName": "PC", "authenticated": true,
                                        "capabilities": ["system.hello", "pc.status"]])
        XCTAssertFalse(hello.supportsEntitlements)
        XCTAssertNil(hello.agentVersion)
    }

    func testEmptyOrMistypedHelloDoesNotCrash() {
        let hello = AgentHello(result: ["protocol": "one", "capabilities": "all", "features": [1, "rgb.sync"]])
        XCTAssertEqual(hello.protocolVersion, 0)
        XCTAssertTrue(hello.capabilities.isEmpty)
        XCTAssertEqual(hello.features, ["rgb.sync"])
    }

    /// metrics.snapshot fixture with an added field still decodes as a JSON object.
    func testMetricsSnapshotWithFutureFieldStillParses() throws {
        let c = try XCTUnwrap(try Self.cases().first { $0.name.hasPrefix("metrics.snapshot") })
        var payload = try XCTUnwrap(c.message["payload"] as? [String: Any])
        payload["futureField"] = 42
        let data = try JSONSerialization.data(withJSONObject: payload)
        let back = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertNotNil(back["cpu"] as? [String: Any])
        XCTAssertEqual(back["futureField"] as? Int, 42)
    }

    // MARK: - error.details

    func testDetailsFromPlanRequired() throws {
        let error = try errorCase("plan.required with details")
        XCTAssertEqual(ServerErrorDetails(json: error["details"]), ServerErrorDetails(feature: "automations", state: "expired"))
    }

    func testDetailsFromPlanLimit() throws {
        let error = try errorCase("plan.limit with details")
        XCTAssertEqual(ServerErrorDetails(json: error["details"]), ServerErrorDetails(feature: "files.transfer", limit: 104_857_600))
    }

    func testDetailsFromRateLimited() throws {
        let error = try errorCase("rate.limited with retryAfter")
        XCTAssertEqual(ServerErrorDetails(json: error["details"])?.retryAfter, 60)
    }

    func testDetailsOptional() throws {
        let error = try errorCase("plan.required without details (details optional)")
        XCTAssertNil(ServerErrorDetails(json: error["details"]))
        XCTAssertNil(ServerErrorDetails(json: ["other": 1]))
    }

    func testServerErrorTextHasNoCode() {
        let error = NexusError.server(code: "plan.required", message: "This feature needs Nexus PC Pro.",
                                      details: ServerErrorDetails(feature: "automations"))
        XCTAssertEqual(error.errorDescription, "This feature needs Nexus PC Pro.")
        XCTAssertEqual(error.code, "plan.required")
        XCTAssertEqual(error.details?.feature, "automations")
        XCTAssertNil(NexusError.server(code: "internal", message: "x").details)
    }
}
