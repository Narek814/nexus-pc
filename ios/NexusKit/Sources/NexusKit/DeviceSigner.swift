import CryptoKit
import Foundation

/// The device's P-256 identity key. On iPhone it lives in the Secure Enclave; tests use a software key.
public protocol DeviceSigner: Sendable {
    /// SubjectPublicKeyInfo DER, sent in pair.request.
    var publicKeySPKI: Data { get }
    /// ECDSA P-256 SHA-256 signature, DER (X9.62), as the agent expects.
    func sign(_ data: Data) throws -> Data
}

public struct SoftwareSigner: DeviceSigner {
    private let key: P256.Signing.PrivateKey

    public init(key: P256.Signing.PrivateKey = P256.Signing.PrivateKey()) {
        self.key = key
    }

    public var publicKeySPKI: Data { key.publicKey.derRepresentation }
    public var rawRepresentation: Data { key.rawRepresentation }

    public func sign(_ data: Data) throws -> Data {
        try key.signature(for: data).derRepresentation
    }
}

/// Key generated inside the Secure Enclave: the private key never leaves the chip. Its
/// `dataRepresentation` is an encrypted handle that only this device can use.
public struct SecureEnclaveSigner: DeviceSigner {
    private let key: SecureEnclave.P256.Signing.PrivateKey

    public init() throws {
        key = try SecureEnclave.P256.Signing.PrivateKey()
    }

    public init(dataRepresentation: Data) throws {
        key = try SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: dataRepresentation)
    }

    public static var isAvailable: Bool { SecureEnclave.isAvailable }

    public var dataRepresentation: Data { key.dataRepresentation }
    public var publicKeySPKI: Data { key.publicKey.derRepresentation }

    public func sign(_ data: Data) throws -> Data {
        try key.signature(for: data).derRepresentation
    }
}
