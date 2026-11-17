import NexusKit
import PhotosUI
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// Files and clipboard (P7). Files go to and come from one folder on the PC: Downloads\Nexus PC.
/// Needs the `files` / `clipboard` permissions, which the PC owner grants explicitly.
struct FilesView: View {
    let controller: PCController

    @State private var transfers: FileTransfers
    @State private var photoItems: [PhotosPickerItem] = []
    @State private var importing = false
    @State private var pcClipboard: String?
    @State private var status: String?

    init(controller: PCController) {
        self.controller = controller
        _transfers = State(initialValue: FileTransfers(controller: controller))
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if let status {
                        Text(status)
                            .font(.footnote)
                            .foregroundStyle(.orange)
                            .textSelection(.enabled)
                    }
                    if controller.can("files.upload.begin") {
                        SectionTitle("Отправить на ПК")
                        sendCard
                    }
                    if let current = transfers.current {
                        progressCard(current)
                    }
                    if controller.can("clipboard.get") {
                        SectionTitle("Буфер обмена")
                        clipboardCard
                    }
                    if controller.can("files.list") {
                        SectionTitle("Папка Nexus PC на ПК")
                        folderCard
                    }
                }
                .padding(20)
            }
            .refreshable { await transfers.refresh() }
        }
        .navigationTitle("Файлы")
        .navigationBarTitleDisplayMode(.inline)
        .task { await transfers.refresh() }
        .onChange(of: transfers.message) { _, message in
            if let message { status = message }
        }
        .onChange(of: photoItems) { _, items in
            guard !items.isEmpty else { return }
            photoItems = []
            Task { await sendPhotos(items) }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            switch result {
            case let .success(urls):
                Task { await sendFiles(urls) }
            case let .failure(error):
                status = error.localizedDescription
            }
        }
        .sheet(item: $transfers.shared) { item in
            ShareSheet(url: item.url)
        }
    }

    // MARK: - Cards

    private var sendCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 12) {
                    PhotosPicker(selection: $photoItems, maxSelectionCount: 20, matching: .any(of: [.images, .videos]), preferredItemEncoding: .compatible) {
                        Label("Фото и видео", systemImage: "photo.on.rectangle")
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 12)
                            .background(Theme.surface, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                    }
                    Button {
                        importing = true
                    } label: {
                        Label("Файлы", systemImage: "folder")
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 12)
                            .background(Theme.surface, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                    }
                }
                .buttonStyle(.plain)
                .foregroundStyle(Theme.cyan)
                .disabled(transfers.current != nil)
                Text("Файлы попадут в «Загрузки\\Nexus PC» на ПК. Фото iPhone отправляются как JPEG, видео — как MP4. Если связь оборвётся, повторная отправка того же файла продолжится с места обрыва. Держите приложение открытым до конца передачи.")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
            }
        }
    }

    private func progressCard(_ transfer: FileTransfers.Progress) -> some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Image(systemName: transfer.upload ? "arrow.up.circle.fill" : "arrow.down.circle.fill")
                        .foregroundStyle(Theme.violet)
                    Text(transfer.name).font(.subheadline.weight(.semibold)).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Отмена") { transfers.cancel() }
                        .foregroundStyle(.red)
                        .font(.footnote)
                }
                ProgressView(value: transfer.fraction).tint(Theme.cyan)
                Text(transfer.caption)
                    .font(.caption)
                    .foregroundStyle(Theme.textMuted)
                    .monospacedDigit()
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    private var clipboardCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 12) {
                if controller.can("clipboard.set") {
                    // PasteButton reads the iPhone clipboard only when tapped (no "Allow Paste" prompt).
                    PasteButton(payloadType: String.self) { strings in
                        guard let text = strings.first, !text.isEmpty else { return }
                        Task { await sendClipboard(text) }
                    }
                    .buttonBorderShape(.capsule)
                    .tint(Theme.violet)
                    Text("Вставляет текст из буфера iPhone в буфер ПК — дальше Ctrl+V на ПК.")
                        .font(.caption2)
                        .foregroundStyle(Theme.textMuted)
                }
                NeonButton(title: "Взять текст из буфера ПК", systemImage: "doc.on.clipboard") {
                    Task { await getClipboard() }
                }
                if let pcClipboard {
                    Text(pcClipboard)
                        .font(.footnote)
                        .lineLimit(6)
                        .textSelection(.enabled)
                        .foregroundStyle(Theme.textPrimary)
                    Text("Скопировано в буфер iPhone.")
                        .font(.caption2)
                        .foregroundStyle(Theme.cyan)
                }
                Text("Только по нажатию: автоматической синхронизации нет. ПК показывает уведомление, когда телефон читает или меняет его буфер.")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
            }
        }
    }

    private var folderCard: some View {
        GlassCard {
            VStack(alignment: .leading, spacing: 10) {
                if transfers.files.isEmpty {
                    Text("Папка пуста. Положите файлы в «Загрузки\\Nexus PC» на ПК (меню значка Nexus PC → «Открыть папку файлов с телефона») и потяните экран вниз.")
                        .font(.caption)
                        .foregroundStyle(Theme.textMuted)
                }
                ForEach(transfers.files) { file in
                    Button {
                        Task { await transfers.download(file) }
                    } label: {
                        HStack(spacing: 12) {
                            Image(systemName: file.symbol).foregroundStyle(Theme.violet).frame(width: 24)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(file.name).font(.subheadline).lineLimit(1).truncationMode(.middle)
                                Text(file.caption).font(.caption2).foregroundStyle(Theme.textMuted)
                            }
                            Spacer()
                            Image(systemName: "arrow.down.circle").foregroundStyle(Theme.cyan)
                        }
                    }
                    .buttonStyle(.plain)
                    .disabled(transfers.current != nil)
                }
                if let free = transfers.freeBytes {
                    Text("Свободно на диске ПК: \(ByteCountFormatter.string(fromByteCount: free, countStyle: .file))")
                        .font(.caption2)
                        .foregroundStyle(Theme.textMuted)
                }
            }
            .foregroundStyle(Theme.textPrimary)
        }
    }

    // MARK: - Actions

    private func sendPhotos(_ items: [PhotosPickerItem]) async {
        status = nil
        for (index, item) in items.enumerated() {
            do {
                guard let picked = try await item.loadTransferable(type: PickedFile.self) else { continue }
                let ok = await transfers.upload(picked.url, name: picked.name, label: "\(index + 1) из \(items.count)")
                try? FileManager.default.removeItem(at: picked.url.deletingLastPathComponent())
                if !ok { break }
            } catch {
                status = "Не удалось прочитать фото: \(error.localizedDescription)"
                break
            }
        }
        await transfers.refresh()
    }

    private func sendFiles(_ urls: [URL]) async {
        status = nil
        for (index, url) in urls.enumerated() {
            // Files from the Files app are security-scoped: copy while access is granted.
            let access = url.startAccessingSecurityScopedResource()
            let copy = try? PickedFile.copyToTemp(url)
            if access { url.stopAccessingSecurityScopedResource() }
            guard let copy else {
                status = "Не удалось открыть «\(url.lastPathComponent)»."
                break
            }
            let ok = await transfers.upload(copy, name: url.lastPathComponent, label: "\(index + 1) из \(urls.count)")
            try? FileManager.default.removeItem(at: copy.deletingLastPathComponent())
            if !ok { break }
        }
        await transfers.refresh()
    }

    private func sendClipboard(_ text: String) async {
        let limited = String(text.prefix(100_000))
        do {
            _ = try await controller.fetch("clipboard.set", payload: ["text": limited])
            status = limited.count < text.count ? "Отправлено на ПК (первые 100 000 символов)." : "Текст в буфере ПК — нажмите Ctrl+V."
        } catch {
            status = FileTransfers.describe(error, controller: controller)
        }
    }

    private func getClipboard() async {
        do {
            let result = try await controller.fetch("clipboard.get")
            if let text = result["text"] as? String, !text.isEmpty {
                UIPasteboard.general.string = text
                pcClipboard = text
                status = nil
            } else {
                pcClipboard = nil
                status = "В буфере ПК нет текста (может быть картинка или файлы)."
            }
        } catch {
            status = FileTransfers.describe(error, controller: controller)
        }
    }
}

/// Runs one transfer at a time and reports progress.
@MainActor
@Observable
final class FileTransfers {
    struct Progress {
        let name: String
        let upload: Bool
        var done: Int64
        let total: Int64
        let started: Date
        var label: String

        var fraction: Double { total == 0 ? 1 : Double(done) / Double(total) }

        var caption: String {
            let elapsed = max(Date().timeIntervalSince(started), 0.001)
            let speed = Double(done) / elapsed
            let doneText = ByteCountFormatter.string(fromByteCount: done, countStyle: .file)
            let totalText = ByteCountFormatter.string(fromByteCount: total, countStyle: .file)
            let speedText = ByteCountFormatter.string(fromByteCount: Int64(speed), countStyle: .file)
            return "\(label) · \(doneText) из \(totalText) · \(speedText)/с"
        }
    }

    struct RemoteFile: Identifiable {
        var id: String { name }
        let name: String
        let size: Int64
        let modified: Date?

        var caption: String {
            var parts = [ByteCountFormatter.string(fromByteCount: size, countStyle: .file)]
            if let modified {
                parts.append(modified.formatted(date: .abbreviated, time: .shortened))
            }
            return parts.joined(separator: " · ")
        }

        var symbol: String {
            let type = UTType(filenameExtension: (name as NSString).pathExtension)
            if type?.conforms(to: .image) == true { return "photo" }
            if type?.conforms(to: .movie) == true { return "film" }
            if type?.conforms(to: .audio) == true { return "music.note" }
            if type?.conforms(to: .archive) == true { return "doc.zipper" }
            return "doc"
        }
    }

    struct SharedURL: Identifiable {
        let id = UUID()
        let url: URL
    }

    private let controller: PCController
    private var cancelled = false
    var current: Progress?
    var files: [RemoteFile] = []
    var freeBytes: Int64?
    var message: String?
    var shared: SharedURL?

    init(controller: PCController) {
        self.controller = controller
    }

    func refresh() async {
        guard controller.can("files.list") else { return }
        do {
            let result = try await controller.fetch("files.list")
            let iso = ISO8601DateFormatter()
            files = (result["files"] as? [[String: Any]] ?? []).compactMap { item in
                guard let name = item["name"] as? String else { return nil }
                let modified = (item["modified"] as? String).flatMap { iso.date(from: $0) }
                return RemoteFile(name: name, size: Self.int64(item["size"]), modified: modified)
            }
            freeBytes = result["freeBytes"] == nil ? nil : Self.int64(result["freeBytes"])
        } catch {
            message = Self.describe(error, controller: controller)
        }
    }

    func cancel() {
        cancelled = true
    }

    /// Returns false if the transfer failed or was cancelled (stop sending the rest).
    func upload(_ url: URL, name: String, label: String) async -> Bool {
        cancelled = false
        message = nil
        let background = UIApplication.shared.beginBackgroundTask(withName: "Nexus PC upload")
        defer { UIApplication.shared.endBackgroundTask(background) }
        do {
            let size = Int64((try FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0)
            current = Progress(name: name, upload: true, done: 0, total: size, started: Date(), label: "\(label) · проверка")
            let sha = try await Task.detached { try FileDigest.sha256Hex(of: url) }.value
            let begin = try await controller.fetch("files.upload.begin", payload: ["name": name, "size": size, "sha256": sha])
            guard let uploadId = begin["uploadId"] as? String else { throw NexusError.protocolViolation("uploadId") }
            var offset = Self.int64(begin["offset"])
            current = Progress(name: name, upload: true, done: offset, total: size, started: Date(), label: offset > 0 ? "\(label) · продолжение" : label)
            while offset < size {
                if cancelled {
                    _ = try? await controller.fetch("files.upload.cancel", payload: ["uploadId": uploadId])
                    current = nil
                    message = "Отправка отменена."
                    return false
                }
                let at = offset
                let chunk = try await Task.detached { try FileDigest.chunk(of: url, at: at) }.value
                if chunk.isEmpty { throw NexusError.protocolViolation("файл стал короче") }
                let result = try await controller.fetch("files.upload.chunk", payload: [
                    "uploadId": uploadId, "offset": offset, "data": chunk.base64EncodedString(),
                ], timeout: 60)
                offset = Self.int64(result["received"])
                current?.done = offset
            }
            current?.label = "\(label) · проверка на ПК"
            let finish = try await controller.fetch("files.upload.finish", payload: ["uploadId": uploadId], timeout: 300)
            current = nil
            message = "Отправлено: \(finish["name"] as? String ?? name)"
            return true
        } catch {
            current = nil
            message = Self.describe(error, controller: controller)
            return false
        }
    }

    func download(_ file: RemoteFile) async {
        cancelled = false
        message = nil
        let background = UIApplication.shared.beginBackgroundTask(withName: "Nexus PC download")
        defer { UIApplication.shared.endBackgroundTask(background) }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let target = folder.appendingPathComponent(file.name)
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            FileManager.default.createFile(atPath: target.path, contents: nil)
            let handle = try FileHandle(forWritingTo: target)
            defer { try? handle.close() }
            current = Progress(name: file.name, upload: false, done: 0, total: file.size, started: Date(), label: "Загрузка")
            var offset: Int64 = 0
            var eof = file.size == 0
            while !eof {
                if cancelled {
                    current = nil
                    try? FileManager.default.removeItem(at: folder)
                    message = "Загрузка отменена."
                    return
                }
                let result = try await controller.fetch("files.read", payload: [
                    "name": file.name, "offset": offset, "length": FileTransferLimits.chunkSize,
                ], timeout: 60)
                guard let text = result["data"] as? String, let data = Data(base64Encoded: text) else {
                    throw NexusError.protocolViolation("data")
                }
                try handle.write(contentsOf: data)
                offset += Int64(data.count)
                eof = result["eof"] as? Bool ?? true
                current = Progress(name: file.name, upload: false, done: offset, total: max(Self.int64(result["size"]), offset), started: current?.started ?? Date(), label: "Загрузка")
            }
            try handle.close()
            current?.label = "Проверка"
            let remote = try await controller.fetch("files.hash", payload: ["name": file.name], timeout: 300)
            let local = try await Task.detached { try FileDigest.sha256Hex(of: target) }.value
            guard remote["sha256"] as? String == local else {
                current = nil
                try? FileManager.default.removeItem(at: folder)
                message = "Файл изменился на ПК во время загрузки или повредился. Попробуйте ещё раз."
                return
            }
            current = nil
            shared = SharedURL(url: target)
        } catch {
            current = nil
            try? FileManager.default.removeItem(at: folder)
            message = Self.describe(error, controller: controller)
        }
    }

    static func int64(_ value: Any?) -> Int64 {
        switch value {
        case let v as Int64: v
        case let v as Int: Int64(v)
        case let v as NSNumber: v.int64Value
        case let v as Double: Int64(v)
        default: 0
        }
    }

    static func describe(_ error: Error, controller: PCController) -> String {
        let nexus = error as? NexusError
        switch nexus?.code {
        case "auth.permissionDenied":
            let id = controller.pc?.deviceId.uuidString.lowercased() ?? "<deviceId>"
            return "Файлы и буфер обмена не разрешены для этого iPhone. На ПК в PowerShell от имени администратора:\n& \"C:\\Program Files\\NexusPC\\Agent\\Nexus.Agent.exe\" devices permissions \(id) power,apps,monitoring,rgb,input,files,clipboard"
        case "session.unavailable":
            return "На ПК не запущен Nexus PC в трее — файлы и буфер обмена работают через него."
        default:
            if case let .server(_, text)? = nexus {
                return text
            }
            return (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}

/// A photo, video or file copied into the app's temporary folder.
struct PickedFile: Transferable {
    let url: URL

    var name: String { url.lastPathComponent }

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .movie) { received in
            PickedFile(url: try copyToTemp(received.file))
        }
        FileRepresentation(importedContentType: .image) { received in
            PickedFile(url: try copyToTemp(received.file))
        }
        FileRepresentation(importedContentType: .item) { received in
            PickedFile(url: try copyToTemp(received.file))
        }
    }

    static func copyToTemp(_ source: URL) throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = folder.appendingPathComponent(source.lastPathComponent)
        try FileManager.default.copyItem(at: source, to: target)
        return target
    }
}

/// System share sheet: "Сохранить в Файлы", "Сохранить изображение", AirDrop…
struct ShareSheet: UIViewControllerRepresentable {
    let url: URL

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: [url], applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}
