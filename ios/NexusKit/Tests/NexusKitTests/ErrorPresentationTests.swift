import XCTest
@testable import NexusKit

/// Table "code → key, style" from docs/design/error-texts.md, sections 3.1–3.7.
final class ErrorPresentationTests: XCTestCase {
    private typealias Row = (code: String, key: String?, style: ErrorPresentation.Style)

    private let table: [Row] = [
        // 3.1
        ("protocol.tooLarge", "error.protocol.tooLarge", .toast),
        ("protocol.malformed", "error.protocol.malformed", .card),
        ("protocol.unsupportedVersion", "error.protocol.unsupportedVersion", .card),
        ("command.unknown", "error.command.unknown", .card),
        ("command.notImplemented", "error.command.notImplemented", .card),
        ("command.invalidPayload", "error.command.invalidPayload", .toast),
        ("auth.required", "error.auth.required", .silent),
        ("auth.failed", "error.auth.failed", .card),
        ("auth.permissionDenied", "permission.title", .permission),
        ("auth.clockSkew", "error.auth.clockSkew", .card),
        ("auth.replay", "error.auth.replay", .toast),
        ("confirm.required", "error.confirm.required", .toast),
        ("auth.confirmationRequired", "error.confirm.required", .toast),
        // 3.2 (without details)
        ("plan.required", "pro.card.title", .pro),
        ("plan.limit", "limit.files.body", .limit),
        // 3.3
        ("pair.noActiveSession", "error.pair.noActiveSession", .card),
        ("pair.invalidCode", "error.pair.invalidCode", .card),
        ("pair.busy", "error.pair.busy", .card),
        ("pair.rejected", "error.pair.rejected", .card),
        ("pair.expired", "error.pair.expired", .card),
        ("pair.deviceLimit", "error.pair.deviceLimit", .card),
        // 3.4
        ("session.unavailable", "status.trayMissing", .tray),
        ("power.failed", "error.power.failed", .toast),
        ("power.nothingToCancel", "error.power.nothingToCancel", .toast),
        ("power.unsupported", "error.power.unsupported", .card),
        ("power.lockFailed", "error.power.lockFailed", .toast),
        ("apps.notFound", "error.apps.notFound", .toast),
        ("apps.launchFailed", "error.apps.launchFailed", .toast),
        ("apps.closeNotAllowed", "error.apps.closeNotAllowed", .toast),
        ("profiles.notFound", "error.profiles.notFound", .toast),
        ("profiles.invalid", "error.profiles.invalid", .inline),
        ("profiles.limit", "error.profiles.limit", .inline),
        // 3.5
        ("rgb.unavailable", "error.rgb.unavailable", .card),
        ("files.notFound", "error.files.notFound", .toast),
        ("files.offsetMismatch", "error.files.resume", .inline),
        ("files.uploadNotFound", "error.files.resume", .inline),
        ("files.hashMismatch", "error.files.hashMismatch", .inline),
        ("files.noSpace", "error.files.noSpace", .inline),
        ("files.failed", "error.files.failed", .inline),
        ("clipboard.failed", "error.clipboard.failed", .toast),
        ("automations.notFound", "error.automations.notFound", .toast),
        ("automations.limit", "error.automations.limit", .inline),
        ("automations.running", "error.automations.running", .toast),
        // 3.6
        ("screen.videoUnavailable", nil, .silent),
        ("screen.denied", "error.screen.denied", .card),
        ("screen.blocked", "error.screen.blocked", .card),
        ("screen.notStarted", nil, .silent),
        ("input.blocked", "error.input.blocked", .toast),
        // 3.7
        ("rate.limited", "error.rateLimited", .toast),
        ("internal", "error.internal", .card),
    ]

    func testEveryDocumentedCode() {
        for row in table {
            let p = ErrorPresentation.for(code: row.code, details: nil, section: nil)
            XCTAssertEqual(p.key, row.key, row.code)
            XCTAssertEqual(p.style, row.style, row.code)
        }
    }

    func testConfirmCodesShareOneKey() {
        XCTAssertEqual(ErrorPresentation.for(code: "confirm.required", details: nil, section: nil),
                       ErrorPresentation.for(code: "auth.confirmationRequired", details: nil, section: "control"))
    }

    func testPlanRequiredByState() {
        XCTAssertEqual(ErrorPresentation.for(code: "plan.required", details: .init(feature: "automations", state: "expired"), section: nil),
                       ErrorPresentation(key: "pro.expired.body", style: .pro))
        XCTAssertEqual(ErrorPresentation.for(code: "plan.required", details: .init(feature: "automations", state: "revoked"), section: nil),
                       ErrorPresentation(key: "pro.revoked.body", style: .pro))
        XCTAssertEqual(ErrorPresentation.for(code: "plan.required", details: .init(feature: "screen.video", state: "none"), section: nil),
                       ErrorPresentation(key: "pro.feature.screen.video.body", style: .pro))
    }

    func testPlanLimitWithNumber() {
        XCTAssertEqual(ErrorPresentation.for(code: "plan.limit", details: .init(feature: "files", limit: 104_857_600), section: "files").key,
                       "limit.files.bodyWithLimit")
    }

    func testSessionUnavailableInControlAndFiles() {
        for section in ["control", "files"] {
            XCTAssertEqual(ErrorPresentation.for(code: "session.unavailable", details: nil, section: section),
                           ErrorPresentation(key: "error.session.signIn", style: .tray), section)
        }
        XCTAssertEqual(ErrorPresentation.for(code: "session.unavailable", details: nil, section: "home").key, "status.trayMissing")
    }

    func testUnknownCode() {
        let p = ErrorPresentation.for(code: "future.code", details: nil, section: nil)
        XCTAssertNil(p.key)
        XCTAssertEqual(p.style, .card)
    }

    func testFallbackMessage() {
        XCTAssertEqual(ErrorPresentation.fallbackMessage("PC says no"), "PC says no")
        XCTAssertNil(ErrorPresentation.fallbackMessage(""))
        XCTAssertNil(ErrorPresentation.fallbackMessage("   "))
        XCTAssertNil(ErrorPresentation.fallbackMessage(nil))
        XCTAssertNil(ErrorPresentation.fallbackMessage(String(repeating: "x", count: 201)))
        XCTAssertNotNil(ErrorPresentation.fallbackMessage(String(repeating: "x", count: 200)))
    }

    // MARK: - Plan features (PlanPolicy.cs)

    func testPlanRequiredKeyForEveryFeature() {
        for feature in ["profiles", "rgb.sync", "automations", "monitoring.extended", "screen.video"] {
            XCTAssertEqual(ErrorPresentation.for(code: "plan.required", details: .init(feature: feature), section: nil),
                           ErrorPresentation(key: "pro.feature.\(feature).body", style: .pro), feature)
        }
    }

    // MARK: - Metric reasons (error-texts.md, 3.8)

    func testEveryMetricReasonCode() {
        let codes = ["sensorDriverMissing", "sensorReadFailed", "sensorMissing", "warmingUp", "osQueryFailed",
                     "gpuDriverMissing", "gpuLibraryRejected", "gpuLibraryLoadFailed", "gpuInitFailed", "gpuListFailed",
                     "platformUnsupported"]
        XCTAssertEqual(codes.count, 11)
        for code in codes {
            XCTAssertEqual(ErrorPresentation.metricReasonKey(code), "monitor.reason.\(code)", code)
            XCTAssertEqual(ErrorPresentation.metricReason(code: code, pcText: "PC text"), .key("monitor.reason.\(code)"), code)
        }
    }

    func testUnknownMetricReason() {
        XCTAssertEqual(ErrorPresentation.metricReasonKey("futureCode"), "monitor.reason.unknown")
        XCTAssertEqual(ErrorPresentation.metricReason(code: "futureCode", pcText: "PC text"), .key("monitor.reason.unknown"))
    }

    func testMetricReasonWithoutCode() {
        XCTAssertEqual(ErrorPresentation.metricReason(code: nil, pcText: "PC text"), .pcText("PC text"))
        XCTAssertEqual(ErrorPresentation.metricReason(code: nil, pcText: "  "), .key("monitor.reason.unknown"))
        XCTAssertEqual(ErrorPresentation.metricReason(code: nil, pcText: nil), .key("monitor.reason.unknown"))
    }

    func testUnavailableCodesFromFixture() throws {
        let c = try XCTUnwrap(try AgentHelloTests.cases().first { $0.name == "metrics.snapshot with unavailable and unavailableCodes" })
        let payload = try XCTUnwrap(c.message["payload"] as? [String: Any])
        let codes = ErrorPresentation.reasonMap(payload["unavailableCodes"])
        let texts = ErrorPresentation.reasonMap(payload["unavailable"])
        XCTAssertFalse(codes.isEmpty)
        for (metric, code) in codes {
            XCTAssertNotNil(texts[metric], metric)
            XCTAssertEqual(ErrorPresentation.metricReason(code: code, pcText: texts[metric]), .key("monitor.reason.\(code)"), metric)
        }
        XCTAssertEqual(codes["cpu.temperature"], "sensorDriverMissing")
        XCTAssertEqual(codes["gpu"], "gpuDriverMissing")
        XCTAssertTrue(ErrorPresentation.reasonMap(nil).isEmpty)
        XCTAssertEqual(ErrorPresentation.reasonMap(["a": "x", "b": 1] as [String: Any]), ["a": "x"])
    }

    // MARK: - Lighting reasons (error-texts.md, 3.9)

    func testRgbReasonKeys() {
        XCTAssertEqual(ErrorPresentation.rgbReasonKey("openrgbNotResponding"), "error.rgb.unavailable")
        XCTAssertEqual(ErrorPresentation.rgbReasonKey("conflict"), "rgb.busy")
        XCTAssertNil(ErrorPresentation.rgbReasonKey("futureCode"))
        XCTAssertNil(ErrorPresentation.rgbReasonKey(nil))
    }

    func testRgbUnavailableText() {
        XCTAssertEqual(ErrorPresentation.rgbUnavailable(RgbStatus(available: false, reason: "PC", reasonCode: "conflict")), .key("rgb.busy"))
        XCTAssertEqual(ErrorPresentation.rgbUnavailable(RgbStatus(available: false, reason: "PC", reasonCode: "openrgbNotResponding")),
                       .key("error.rgb.unavailable"))
        XCTAssertEqual(ErrorPresentation.rgbUnavailable(RgbStatus(available: false, reason: "PC", state: "conflict")), .key("rgb.busy"))
        XCTAssertEqual(ErrorPresentation.rgbUnavailable(RgbStatus(available: false, reason: "PC", reasonCode: "futureCode")), .pcText("PC"))
        XCTAssertEqual(ErrorPresentation.rgbUnavailable(RgbStatus(available: false)), .key("error.rgb.unavailable"))
    }
}
