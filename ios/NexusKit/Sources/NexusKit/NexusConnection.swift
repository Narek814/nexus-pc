import CryptoKit
import Foundation
import Security

public struct NexusResponse: @unchecked Sendable {
    public let ok: Bool
    public let payload: [String: Any]
    public let errorCode: String?
    public let errorMessage: String?

    /// Payload for ok responses; throws `NexusError.server` otherwise.
    public func unwrap() throws -> [String: Any] {
        guard ok else {
            throw NexusError.server(code: errorCode ?? "internal", message: errorMessage ?? "Ошибка")
        }
        return payload
    }
}

/// One TLS WebSocket connection to Nexus.Agent (docs/API.md). The certificate is pinned to the
/// fingerprint from the pairing link: any other certificate fails the connection. Requests may run
/// concurrently; responses are matched by id.
public final class NexusConnection: NSObject, @unchecked Sendable {
    private let session: URLSession
    private let task: URLSessionWebSocketTask
    private let lock = NSLock()
    private var pending: [String: CheckedContinuation<NexusResponse, Error>] = [:]
    private var closedError: NexusError?

    private init(url: URL, fingerprint: String) {
        let delegate = PinningDelegate(fingerprint: fingerprint)
        let configuration = URLSessionConfiguration.ephemeral
        // Always direct: a proxy must never relay traffic to the PC.
        configuration.connectionProxyDictionary = [:]
        configuration.waitsForConnectivity = false
        session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
        task = session.webSocketTask(with: url)
        // File chunks: 512 KiB as base64 plus the envelope (the agent allows 1 MiB per message).
        task.maximumMessageSize = 2 * 1024 * 1024
        super.init()
    }

    /// Opens the connection and checks it with system.hello. Returns the hello payload too.
    public static func connect(host: String, port: Int, fingerprint: String, timeout: TimeInterval = 5) async throws -> (NexusConnection, [String: Any]) {
        let hostPart = host.contains(":") ? "[\(host)]" : host
        guard let url = URL(string: "wss://\(hostPart):\(port)/v1/ws") else {
            throw NexusError.connection("Неверный адрес \(host)")
        }
        let connection = NexusConnection(url: url, fingerprint: fingerprint)
        connection.task.resume()
        connection.startReceiving()
        do {
            let hello = try await connection.request("system.hello", timeout: timeout).unwrap()
            return (connection, hello)
        } catch {
            connection.close()
            throw error
        }
    }

    /// Sends one request and waits for its response.
    public func request(_ type: String, payload: [String: Any] = [:], confirmed: Bool = false, timeout: TimeInterval = 15) async throws -> NexusResponse {
        let id = UUID()
        let key = id.uuidString.lowercased()
        let data = try RequestEnvelope.make(type: type, payload: payload, confirmed: confirmed, id: id)
        guard let text = String(data: data, encoding: .utf8) else {
            throw NexusError.protocolViolation("encoding")
        }

        return try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if let error = closedError {
                lock.unlock()
                continuation.resume(throwing: error)
                return
            }
            pending[key] = continuation
            lock.unlock()

            task.send(.string(text)) { [weak self] error in
                if let error {
                    self?.fail(key, NexusError.connection(error.localizedDescription))
                }
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { [weak self] in
                self?.fail(key, NexusError.timeout)
            }
        }
    }

    /// pair.request: waits (up to ~2 minutes) for the person at the PC to approve.
    public func pair(code: String, deviceName: String, signer: DeviceSigner) async throws -> (deviceId: UUID, permissions: [String]) {
        let payload = try await request("pair.request", payload: [
            "code": code,
            "deviceName": deviceName,
            "publicKey": signer.publicKeySPKI.base64EncodedString(),
        ], timeout: 150).unwrap()
        guard let idText = payload["deviceId"] as? String, let deviceId = UUID(uuidString: idText) else {
            throw NexusError.protocolViolation("deviceId")
        }
        return (deviceId, payload["permissions"] as? [String] ?? [])
    }

    /// auth.begin → sign the transcript → auth.prove. Returns granted permissions.
    @discardableResult
    public func authenticate(pcId: UUID, deviceId: UUID, fingerprint: String, signer: DeviceSigner) async throws -> [String] {
        let begin = try await request("auth.begin", payload: ["deviceId": deviceId.uuidString.lowercased()]).unwrap()
        guard let nonceText = begin["nonce"] as? String, let nonce = Data(base64Encoded: nonceText) else {
            throw NexusError.protocolViolation("nonce")
        }
        let transcript = DeviceCrypto.authTranscript(pcId: pcId, deviceId: deviceId, nonce: nonce, fingerprint: fingerprint)
        let signature = try signer.sign(transcript)
        let proved = try await request("auth.prove", payload: ["signature": signature.base64EncodedString()]).unwrap()
        return proved["permissions"] as? [String] ?? []
    }

    public func close() {
        task.cancel(with: .normalClosure, reason: nil)
        session.invalidateAndCancel()
        failAll(NexusError.connection("Соединение закрыто."))
    }

    private func startReceiving() {
        task.receive { [weak self] result in
            guard let self else {
                return
            }
            switch result {
            case let .success(message):
                self.handle(message)
                self.startReceiving()
            case let .failure(error):
                self.failAll(NexusError.connection(Self.describe(error)))
            }
        }
    }

    private func handle(_ message: URLSessionWebSocketTask.Message) {
        let data: Data
        switch message {
        case let .string(text):
            data = Data(text.utf8)
        case let .data(bytes):
            data = bytes
        @unknown default:
            return
        }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["kind"] as? String == "response",
              let id = object["id"] as? String
        else {
            return // events (future) and unparseable messages are ignored here
        }
        let error = object["error"] as? [String: Any]
        let response = NexusResponse(
            ok: object["ok"] as? Bool ?? false,
            payload: object["payload"] as? [String: Any] ?? [:],
            errorCode: error?["code"] as? String,
            errorMessage: error?["message"] as? String)
        lock.lock()
        let continuation = pending.removeValue(forKey: id)
        lock.unlock()
        continuation?.resume(returning: response)
    }

    private func fail(_ key: String, _ error: NexusError) {
        lock.lock()
        let continuation = pending.removeValue(forKey: key)
        lock.unlock()
        continuation?.resume(throwing: error)
    }

    private func failAll(_ error: NexusError) {
        lock.lock()
        if closedError == nil {
            closedError = error
        }
        let all = pending
        pending.removeAll()
        lock.unlock()
        for continuation in all.values {
            continuation.resume(throwing: error)
        }
    }

    private static func describe(_ error: Error) -> String {
        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain {
            switch nsError.code {
            case NSURLErrorCancelled, NSURLErrorServerCertificateUntrusted, NSURLErrorSecureConnectionFailed:
                return "ПК не подтвердил свой сертификат. Проверьте, что это ваш ПК, или подключите его заново."
            case NSURLErrorTimedOut, NSURLErrorCannotConnectToHost, NSURLErrorNetworkConnectionLost, NSURLErrorNotConnectedToInternet:
                return "Нет связи с ПК. ПК включён и в той же сети?"
            default:
                break
            }
        }
        return error.localizedDescription
    }
}

/// Accepts exactly one certificate: the one whose SHA-256 matches the pinned fingerprint.
final class PinningDelegate: NSObject, URLSessionDelegate, @unchecked Sendable {
    private let fingerprint: String

    init(fingerprint: String) {
        self.fingerprint = fingerprint.uppercased()
    }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first
        else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        let der = SecCertificateCopyData(leaf) as Data
        if DeviceCrypto.certificateFingerprint(der: der) == fingerprint {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.cancelAuthenticationChallenge, nil)
        }
    }
}
