import Foundation
import NexusKit
import Observation
import UIKit

/// Pairing with a PC from its QR code (or pasted link): connect with the pinned certificate,
/// show the verification code, wait for approval on the PC, store the key and the PC.
@MainActor
@Observable
final class PairingFlowModel {
    enum Step: Equatable {
        case scanning
        case connecting(PairingLink)
        case waitingForApproval(PairingLink, code: String)
        case done(PairedPC)
        case failed(String)
    }

    private(set) var step: Step = .scanning
    private var handled = false

    func handle(scanned text: String) {
        guard !handled else {
            return
        }
        guard let link = PairingLink(text.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            step = .failed("Это не QR-код Nexus PC. Откройте на ПК: значок Nexus PC → «Подключить iPhone…».")
            return
        }
        handled = true
        Task { await pair(with: link) }
    }

    func retry() {
        handled = false
        step = .scanning
    }

    private func pair(with link: PairingLink) async {
        step = .connecting(link)
        do {
            let (signer, stored) = try DeviceIdentity.create()
            let code = DeviceCrypto.verificationCode(publicKey: signer.publicKeySPKI, fingerprint: link.certificateFingerprint)
            var lastError: Error = NexusError.connection("Нет связи с ПК.")
            for host in link.hosts {
                do {
                    let (connection, _) = try await NexusConnection.connect(host: host, port: link.port, fingerprint: link.certificateFingerprint, timeout: 4)
                    defer { connection.close() }
                    step = .waitingForApproval(link, code: code)
                    let result = try await connection.pair(code: link.code, deviceName: UIDevice.current.name, signer: signer)
                    let pc = PairedPC(link: link, deviceId: result.deviceId, permissions: result.permissions)
                    try PairingStore.save(pc, key: stored)
                    step = .done(pc)
                    return
                } catch let error as NexusError {
                    lastError = error
                    if case .server = error {
                        break // the PC answered: trying other addresses will not help
                    }
                }
            }
            step = .failed(Self.describe(lastError))
        } catch {
            step = .failed(Self.describe(error))
        }
    }

    private static func describe(_ error: Error) -> String {
        switch (error as? NexusError)?.code {
        case "pair.rejected": return "На ПК нажали «Отклонить»."
        case "pair.expired", "pair.noActiveSession": return "Время сопряжения вышло. Откройте окно на ПК заново."
        case "pair.invalidCode": return "Код не подошёл. Откройте окно сопряжения на ПК заново."
        case "pair.busy": return "Другое устройство уже ждёт подтверждения."
        default: return (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}
