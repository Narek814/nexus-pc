import AVFoundation
import CoreMedia
import NexusKit
import SwiftUI
import UIKit

/// Remote screen (P5): JPEG frames of the PC's main monitor over the existing encrypted connection,
/// with touch control, pinch zoom and a key row. The PC asks its owner once per device; a notice
/// shows while someone watches.
struct ScreenView: View {
    let controller: PCController

    @State private var stream: ScreenStream
    @State private var sender: InputSender
    @State private var typing = false
    @State private var fullscreen = false
    @State private var showKeys = false
    @State private var zoomed = false
    @State private var resetZoom = 0
    @State private var proSheet: ProLock?
    @Environment(\.scenePhase) private var scenePhase

    /// `screen.video` is Pro on this PC (only agents with the entitlements.v1 marker); otherwise `nil`.
    private var videoLock: ProLock? {
        ProLock.check(controller.hello, feature: ProFeature.screenVideo)
    }

    init(controller: PCController) {
        self.controller = controller
        _stream = State(initialValue: ScreenStream(controller: controller))
        _sender = State(initialValue: InputSender(controller: controller))
    }

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Color.black.ignoresSafeArea()
                picture
                    .ignoresSafeArea(edges: fullscreen ? .all : [])
                VStack {
                    HStack(alignment: .top) {
                        if fullscreen {
                            fullscreenButtons
                        }
                        Spacer()
                        if zoomed {
                            overlayButton("1:1", systemImage: nil) { resetZoom += 1 }
                                .padding(8)
                        }
                    }
                    Spacer()
                    if fullscreen && showKeys {
                        ScreenKeysRow(sender: sender)
                            .padding(8)
                            .background(.black.opacity(0.45))
                    }
                }
            }
            if !fullscreen {
                controls
            }
        }
        .background(fullscreen ? Color.black : Theme.background)
        .navigationTitle("Экран ПК")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar(fullscreen ? .hidden : .visible, for: .navigationBar)
        .statusBarHidden(fullscreen)
        .persistentSystemOverlays(fullscreen ? .hidden : .automatic)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    typing.toggle()
                } label: {
                    Image(systemName: typing ? "keyboard.chevron.compact.down" : "keyboard")
                }
                .tint(Theme.cyan)
            }
        }
        .task {
            if videoLock != nil { stream.mode = .jpeg }
            await stream.run()
        }
        .onChange(of: videoLock) { _, lock in
            if lock != nil, stream.mode == .video { stream.mode = .jpeg }
        }
        .sheet(item: $proSheet) { lock in
            ProCardSheet(lock: lock)
        }
        .onDisappear {
            stream.stop()
            if fullscreen { Self.rotate(landscape: false) }
        }
        .onChange(of: scenePhase) { _, phase in
            if phase != .active { stream.stop() }
        }
    }

    @ViewBuilder
    private var picture: some View {
        if let content = stream.content {
            ScreenCanvas(content: content, stream: stream, sender: sender, typing: typing, resetToken: resetZoom, zoomed: $zoomed)
        } else {
            VStack(spacing: 12) {
                ProgressView().tint(Theme.cyan)
                Text(stream.message ?? "Подключение…")
                    .font(.footnote)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(Theme.textMuted)
                    .padding(.horizontal, 24)
            }
        }
    }

    /// Faint buttons over the picture in full screen: exit, keyboard, key row, stats.
    private var fullscreenButtons: some View {
        HStack(spacing: 10) {
            overlayButton(nil, systemImage: "arrow.down.right.and.arrow.up.left") { setFullscreen(false) }
                .accessibilityLabel("Выйти из полноэкранного режима")
            overlayButton(nil, systemImage: typing ? "keyboard.chevron.compact.down" : "keyboard") { typing.toggle() }
                .accessibilityLabel("Клавиатура")
            overlayButton(nil, systemImage: "command") { showKeys.toggle() }
                .accessibilityLabel("Кнопки клавиш")
            Text(stream.stats)
                .font(.caption2.monospacedDigit())
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(.black.opacity(0.45), in: Capsule())
                .foregroundStyle(.white.opacity(0.85))
        }
        .padding(8)
    }

    private func overlayButton(_ title: String?, systemImage: String?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Group {
                if let systemImage {
                    Image(systemName: systemImage)
                } else {
                    Text(title ?? "").font(.caption.weight(.bold))
                }
            }
            .frame(minWidth: 20, minHeight: 20)
            .padding(10)
            .background(.black.opacity(0.45), in: Capsule())
        }
        .foregroundStyle(.white.opacity(0.85))
    }

    private var controls: some View {
        VStack(spacing: 6) {
            if let message = sender.message ?? (stream.content != nil ? stream.message : nil) {
                Text(message).font(.caption2).foregroundStyle(.orange).lineLimit(3)
            }
            ScreenKeysRow(sender: sender)
            HStack(spacing: 10) {
                Button {
                    setFullscreen(true)
                } label: {
                    Image(systemName: "arrow.up.left.and.arrow.down.right")
                        .padding(8)
                        .background(Theme.surface, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                }
                .tint(Theme.cyan)
                .accessibilityLabel("На весь экран")
                Picker("Качество", selection: $stream.preset) {
                    ForEach(ScreenStream.Preset.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 260)
                if let lock = videoLock {
                    // Free on an agent with plans: JPEG only; the video option opens ProCard.
                    Text(verbatim: "JPEG")
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 6)
                        .background(Theme.surface, in: Capsule())
                        .foregroundStyle(Theme.cyan)
                    ProSegment(Text(verbatim: "H.264")) { proSheet = lock }
                        .fixedSize()
                } else {
                    Menu {
                        Picker("Способ", selection: $stream.mode) {
                            Text("Видео H.264").tag(ScreenStream.Mode.video)
                            Text("Кадры JPEG").tag(ScreenStream.Mode.jpeg)
                        }
                    } label: {
                        Text(stream.mode == .video ? "H.264" : "JPEG")
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 6)
                            .background(Theme.surface, in: Capsule())
                    }
                    .tint(Theme.cyan)
                }
                Spacer()
                Text(stream.stats)
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(Theme.textMuted)
            }
            Text("Касание — клик · удержание — правый клик · вести пальцем — перетаскивание · два пальца — прокрутка · развести пальцы — приближение")
                .font(.caption2)
                .foregroundStyle(Theme.textMuted)
                .lineLimit(2)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private func setFullscreen(_ on: Bool) {
        withAnimation(.easeInOut(duration: 0.2)) { fullscreen = on }
        Self.rotate(landscape: on)
    }

    /// Turns the app to landscape (or back), even with the iPhone's rotation lock on.
    private static func rotate(landscape: Bool) {
        guard let scene = UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene }).first else { return }
        scene.keyWindow?.rootViewController?.setNeedsUpdateOfSupportedInterfaceOrientations()
        scene.requestGeometryUpdate(.iOS(interfaceOrientations: landscape ? .landscapeRight : .portrait))
    }
}

/// Keys and shortcuts that the iPhone keyboard does not have.
struct ScreenKeysRow: View {
    let sender: InputSender

    struct Chip: Identifiable, Sendable {
        let title: String
        let command: InputCommand
        var id: String { title }
    }

    private static let keys: [Chip] = [
        Chip(title: "Esc", command: .key("escape")), Chip(title: "Tab", command: .key("tab")), Chip(title: "Enter", command: .key("enter")), Chip(title: "⌫", command: .key("backspace")), Chip(title: "Del", command: .key("delete")),
        Chip(title: "←", command: .key("left")), Chip(title: "↑", command: .key("up")), Chip(title: "↓", command: .key("down")), Chip(title: "→", command: .key("right")),
        Chip(title: "Win", command: .key("win")), Chip(title: "Alt+Tab", command: .key("tab", modifiers: ["alt"])), Chip(title: "Win+D", command: .key("d", modifiers: ["win"])),
        Chip(title: "Ctrl+C", command: .key("c", modifiers: ["ctrl"])), Chip(title: "Ctrl+V", command: .key("v", modifiers: ["ctrl"])), Chip(title: "Ctrl+Z", command: .key("z", modifiers: ["ctrl"])),
        Chip(title: "Alt+F4", command: .key("f4", modifiers: ["alt"])), Chip(title: "F5", command: .key("f5")), Chip(title: "F11", command: .key("f11")),
    ]

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(Self.keys) { chip in
                    Button {
                        UIImpactFeedbackGenerator(style: .light).impactOccurred()
                        sender.send(chip.command)
                    } label: {
                        Text(chip.title)
                            .font(.footnote.weight(.semibold))
                            .padding(.horizontal, 12)
                            .padding(.vertical, 8)
                            .background(Theme.surface.opacity(0.9), in: Capsule())
                            .overlay(Capsule().strokeBorder(Theme.violet.opacity(0.5), lineWidth: 1))
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(Theme.textPrimary)
                }
            }
        }
    }
}

/// What the screen shows: a JPEG picture or the H.264 video of a given size.
enum ScreenContent: Equatable {
    case image(UIImage)
    case video(CGSize)
}

/// Shows the PC screen. Video (H.264) by default: frames are fetched over a second connection of their
/// own, so touches and keys never wait behind video. Falls back to JPEG pictures if the PC has no
/// encoder. One request at a time: a slow network lowers the frame rate instead of building up delay.
@MainActor
@Observable
final class ScreenStream {
    enum Mode: String {
        case video, jpeg
    }

    enum Preset: String, CaseIterable, Identifiable {
        case fast, normal, sharp
        var id: String { rawValue }
        var title: String {
            switch self {
            case .fast: "Быстро"
            case .normal: "Обычно"
            case .sharp: "Чётко"
            }
        }
        var maxWidth: Int {
            switch self {
            case .fast: 960
            case .normal: 1280
            case .sharp: 1920
            }
        }
        var quality: Int {
            switch self {
            case .fast: 45
            case .normal: 60
            case .sharp: 75
            }
        }
        var bitrateKbps: Int {
            switch self {
            case .fast: 1500
            case .normal: 3000
            case .sharp: 6000
            }
        }
    }

    private let controller: PCController
    private var running = false
    private var generation = 0
    private var hash: String?
    private var frameTimes: [(Date, Int)] = []
    private var lastRtt: TimeInterval = 0
    private var videoConnection: NexusConnection?
    private var needKeyframe = true
    private var lastSeq = -1
    var content: ScreenContent?
    var message: String?
    /// Set by the canvas: hands an access unit to the decoder; false if the decoder needs a keyframe.
    @ObservationIgnored var videoSink: ((Data) -> Bool)?
    var preset: Preset = .normal {
        didSet { restartLoop() }
    }
    var mode: Mode = .video {
        didSet { restartLoop() }
    }

    init(controller: PCController) {
        self.controller = controller
    }

    var stats: String {
        let now = Date()
        let recent = frameTimes.filter { now.timeIntervalSince($0.0) <= 1 }
        let kb = recent.reduce(0) { $0 + $1.1 } / 1024
        let rate = mode == .video ? "\(kb) КБ/с" : "\((recent.last?.1 ?? 0) / 1024) КБ"
        return "\(recent.count) к/с · \(Int(lastRtt * 1000)) мс · \(rate)"
    }

    func run() async {
        guard !running else { return }
        running = true
        message = "Ждём разрешения на ПК… Если на ПК появилось окно «показ экрана», нажмите «Да»."
        do {
            _ = try await controller.fetch("screen.start", timeout: 125)
            message = nil
        } catch {
            message = Self.describe(error, controller: controller)
            running = false
            return
        }
        await loop(generation)
    }

    func stop() {
        guard running else { return }
        running = false
        generation += 1
        videoConnection?.close()
        videoConnection = nil
        Task { _ = try? await controller.fetch("screen.stop") }
    }

    private func restartLoop() {
        guard running else { return }
        generation += 1
        hash = nil
        lastSeq = -1
        needKeyframe = true
        let current = generation
        Task { await loop(current) }
    }

    private func loop(_ token: Int) async {
        while running, token == generation {
            if mode == .video {
                await videoStep(token)
            } else {
                await jpegStep()
            }
        }
    }

    private func videoStep(_ token: Int) async {
        let started = Date()
        do {
            if videoConnection == nil, let pc = controller.pc {
                videoConnection = try await PCSession.connect(pc)
            }
            guard let connection = videoConnection else { throw NexusError.connection("ПК не подключён.") }
            var payload: [String: Any] = ["after": lastSeq, "maxWidth": preset.maxWidth, "bitrateKbps": preset.bitrateKbps]
            if needKeyframe { payload["keyframe"] = true }
            let result = try await connection.request("screen.video", payload: payload, timeout: 10).unwrap()
            guard token == generation else { return }
            lastRtt = Date().timeIntervalSince(started)
            needKeyframe = false
            if let width = result["width"] as? Int, let height = result["height"] as? Int {
                let size = CGSize(width: width, height: height)
                if content != .video(size) { content = .video(size) }
            }
            for frame in result["frames"] as? [[String: Any]] ?? [] {
                guard let seq = frame["seq"] as? Int, let text = frame["data"] as? String, let data = Data(base64Encoded: text) else { continue }
                lastSeq = seq
                if !(videoSink?(data) ?? false) {
                    needKeyframe = true
                }
                frameTimes.append((Date(), data.count))
            }
            frameTimes.removeAll { Date().timeIntervalSince($0.0) > 2 }
            if message != nil { message = nil }
        } catch let error as NexusError where error.code == "screen.videoUnavailable" {
            if case let .server(_, text, _) = error { message = text }
            mode = .jpeg // the PC has no H.264 encoder
        } catch let error as NexusError where error.code == "plan.required" {
            // Video is Pro on this PC: silently back to JPEG frames, the event goes to Diagnostics only.
            if case let .server(code, text, details) = error {
                controller.record(code: code, details: details, message: text, section: "control")
            }
            mode = .jpeg
        } catch let error as NexusError where error.code == "screen.notStarted" {
            _ = try? await controller.fetch("screen.start", timeout: 125)
        } catch {
            message = Self.describe(error, controller: controller)
            videoConnection?.close()
            videoConnection = nil
            needKeyframe = true
            try? await Task.sleep(nanoseconds: 1_000_000_000)
        }
    }

    private func jpegStep() async {
        let started = Date()
        var payload: [String: Any] = ["maxWidth": preset.maxWidth, "quality": preset.quality]
        if let hash { payload["known"] = hash }
        do {
            let frame = try await controller.fetch("screen.frame", payload: payload, timeout: 15)
            lastRtt = Date().timeIntervalSince(started)
            hash = frame["hash"] as? String
            if let base64 = frame["jpeg"] as? String, let data = Data(base64Encoded: base64) {
                if let decoded = await Task.detached(operation: { UIImage(data: data)?.preparingForDisplay() }).value {
                    content = .image(decoded)
                }
                frameTimes.append((Date(), data.count))
                frameTimes.removeAll { Date().timeIntervalSince($0.0) > 2 }
            } else {
                try? await Task.sleep(nanoseconds: 60_000_000) // nothing changed on the PC
            }
            if message != nil { message = nil }
        } catch let error as NexusError where error.code == "screen.notStarted" {
            _ = try? await controller.fetch("screen.start", timeout: 125)
        } catch {
            message = Self.describe(error, controller: controller)
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            if case .connection? = error as? NexusError { running = false }
        }
    }

    static func describe(_ error: Error, controller: PCController) -> String {
        let nexus = error as? NexusError
        switch nexus?.code {
        case "auth.permissionDenied":
            return "Показ экрана не разрешён для этого iPhone. На ПК: значок Nexus PC → «Устройства и разрешения…» → отметьте «Показ экрана» и «Тачпад и клавиатура»."
        case "session.unavailable":
            return "На ПК не запущен Nexus PC в трее — экран показывается через него."
        case "screen.denied", "screen.blocked":
            if case let .server(_, text, _)? = nexus { return text }
            return "Показ экрана запрещён на ПК."
        default:
            if case .timeout? = nexus { return "ПК не ответил: возможно, окно разрешения на ПК ещё открыто." }
            if case let .server(_, text, _)? = nexus { return text }
            return (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}

/// The picture and its touch layer in one UIKit view, so taps map to the PC exactly even when zoomed.
struct ScreenCanvas: UIViewRepresentable {
    let content: ScreenContent
    let stream: ScreenStream
    let sender: InputSender
    let typing: Bool
    let resetToken: Int
    @Binding var zoomed: Bool

    final class Coordinator {
        var resetToken = 0
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> ScreenCanvasView {
        let view = ScreenCanvasView()
        context.coordinator.resetToken = resetToken
        configure(view)
        return view
    }

    func updateUIView(_ view: ScreenCanvasView, context: Context) {
        configure(view)
        if context.coordinator.resetToken != resetToken {
            context.coordinator.resetToken = resetToken
            view.resetZoom()
        }
        if typing, !view.isFirstResponder {
            view.becomeFirstResponder()
        } else if !typing, view.isFirstResponder {
            view.resignFirstResponder()
        }
    }

    private func configure(_ view: ScreenCanvasView) {
        switch content {
        case let .image(image):
            view.showImage(image)
        case let .video(size):
            view.showVideo(size: size)
            stream.videoSink = { [weak view] data in view?.enqueueVideo(data) ?? false }
        }
        let sender = sender
        let zoomed = $zoomed
        view.onPoint = { x, y in sender.send(.moveTo(x: x, y: y)) }
        view.onButton = { button, action in sender.send(.button(button, action: action)) }
        view.onScroll = { dx, dy in sender.scroll(dx: dx, dy: dy) }
        view.onText = { text in sender.send(.text(text)) }
        view.onKey = { key in sender.send(.key(key)) }
        view.onZoomChanged = { isZoomed in
            if zoomed.wrappedValue != isZoomed { zoomed.wrappedValue = isZoomed }
        }
    }
}

final class ScreenCanvasView: UIView, UIKeyInput, UIGestureRecognizerDelegate {
    static let maxZoom: CGFloat = 5

    var onPoint: ((Int, Int) -> Void)?
    var onButton: ((String, String) -> Void)?
    var onScroll: ((Double, Double) -> Void)?
    var onText: ((String) -> Void)?
    var onKey: ((String) -> Void)?
    var onZoomChanged: ((Bool) -> Void)?

    private let imageView = UIImageView()
    private let videoView = H264VideoView()
    private var contentSize = CGSize.zero
    private var scale: CGFloat = 1
    private var offset = CGPoint.zero
    private var lastScroll = CGPoint.zero
    private var pinchStartScale: CGFloat = 1
    private var pinchStartOffset = CGPoint.zero
    private var pinchStartCentroid = CGPoint.zero
    private var zooming = false
    private let haptic = UIImpactFeedbackGenerator(style: .light)

    override init(frame: CGRect) {
        super.init(frame: frame)
        isMultipleTouchEnabled = true
        clipsToBounds = true
        backgroundColor = .clear
        imageView.contentMode = .scaleToFill
        imageView.layer.magnificationFilter = .linear
        addSubview(imageView)
        videoView.isHidden = true
        addSubview(videoView)

        let tap = UITapGestureRecognizer(target: self, action: #selector(handleTap(_:)))
        addGestureRecognizer(tap)
        let press = UILongPressGestureRecognizer(target: self, action: #selector(handlePress(_:)))
        press.minimumPressDuration = 0.5
        addGestureRecognizer(press)
        let drag = UIPanGestureRecognizer(target: self, action: #selector(handleDrag(_:)))
        drag.maximumNumberOfTouches = 1
        addGestureRecognizer(drag)
        let scroll = UIPanGestureRecognizer(target: self, action: #selector(handleScroll(_:)))
        scroll.minimumNumberOfTouches = 2
        scroll.maximumNumberOfTouches = 2
        scroll.delegate = self
        addGestureRecognizer(scroll)
        let pinch = UIPinchGestureRecognizer(target: self, action: #selector(handlePinch(_:)))
        pinch.delegate = self
        addGestureRecognizer(pinch)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not used")
    }

    // MARK: - Geometry

    func showImage(_ image: UIImage) {
        if imageView.image !== image { imageView.image = image }
        imageView.isHidden = false
        videoView.isHidden = true
        setContentSize(image.size)
    }

    func showVideo(size: CGSize) {
        imageView.isHidden = true
        imageView.image = nil
        videoView.isHidden = false
        setContentSize(size)
    }

    /// One H.264 access unit; false if the decoder needs a keyframe.
    func enqueueVideo(_ data: Data) -> Bool {
        videoView.enqueue(data)
    }

    private func setContentSize(_ size: CGSize) {
        if size != contentSize {
            contentSize = size
            setNeedsLayout()
        }
    }

    /// The picture fitted into the view at zoom 1.
    private var fittedRect: CGRect {
        let size = contentSize
        guard size.width > 0, size.height > 0, bounds.width > 0 else { return bounds }
        let s = min(bounds.width / size.width, bounds.height / size.height)
        let w = size.width * s
        let h = size.height * s
        return CGRect(x: (bounds.width - w) / 2, y: (bounds.height - h) / 2, width: w, height: h)
    }

    /// Where the picture is drawn now (zoom and pan applied).
    private var displayedRect: CGRect {
        let f = fittedRect
        let w = f.width * scale
        let h = f.height * scale
        return CGRect(x: bounds.midX + offset.x - w / 2, y: bounds.midY + offset.y - h / 2, width: w, height: h)
    }

    /// Keeps a zoomed picture covering the view (no empty space dragged in).
    private func clampOffset() {
        guard scale > 1 else {
            offset = .zero
            return
        }
        let f = fittedRect
        let maxX = max(0, (f.width * scale - bounds.width) / 2)
        let maxY = max(0, (f.height * scale - bounds.height) / 2)
        offset.x = min(max(offset.x, -maxX), maxX)
        offset.y = min(max(offset.y, -maxY), maxY)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        clampOffset()
        imageView.frame = displayedRect
        videoView.frame = displayedRect
    }

    func resetZoom() {
        scale = 1
        offset = .zero
        setNeedsLayout()
        onZoomChanged?(false)
    }

    private func point(_ location: CGPoint) {
        let r = displayedRect
        guard r.width > 0, r.height > 0 else { return }
        let x = Int((min(max((location.x - r.minX) / r.width, 0), 1) * 65535).rounded())
        let y = Int((min(max((location.y - r.minY) / r.height, 0), 1) * 65535).rounded())
        onPoint?(x, y)
    }

    // MARK: - Gestures

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        // Pinch and two-finger pan run together: a real pinch zooms (and pans), a parallel drag scrolls the PC.
        (gestureRecognizer is UIPinchGestureRecognizer && other is UIPanGestureRecognizer)
            || (gestureRecognizer is UIPanGestureRecognizer && other is UIPinchGestureRecognizer)
    }

    @objc private func handleTap(_ g: UITapGestureRecognizer) {
        haptic.impactOccurred()
        point(g.location(in: self))
        onButton?("left", "click")
    }

    @objc private func handlePress(_ g: UILongPressGestureRecognizer) {
        guard g.state == .began else { return }
        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
        point(g.location(in: self))
        onButton?("right", "click")
    }

    @objc private func handleDrag(_ g: UIPanGestureRecognizer) {
        switch g.state {
        case .began:
            point(g.location(in: self))
            onButton?("left", "down")
        case .changed:
            point(g.location(in: self))
        default:
            point(g.location(in: self))
            onButton?("left", "up")
        }
    }

    @objc private func handleScroll(_ g: UIPanGestureRecognizer) {
        let t = g.translation(in: self)
        if zooming {
            lastScroll = t
            return
        }
        switch g.state {
        case .began:
            lastScroll = t
        case .changed:
            onScroll?(-Double(t.x - lastScroll.x) * 3, Double(t.y - lastScroll.y) * 3)
            lastScroll = t
        default:
            lastScroll = .zero
        }
    }

    @objc private func handlePinch(_ g: UIPinchGestureRecognizer) {
        switch g.state {
        case .began:
            pinchStartScale = scale
            pinchStartOffset = offset
            pinchStartCentroid = g.location(in: self)
            zooming = false
        case .changed:
            // Below this the fingers are just moving together: that is scrolling, not zooming.
            if !zooming, abs(log(g.scale)) > 0.08 {
                zooming = true
            }
            guard zooming else { return }
            let newScale = min(max(pinchStartScale * g.scale, 1), Self.maxZoom)
            let ratio = newScale / pinchStartScale
            // Keep the point under the fingers in place, and follow the fingers as they move.
            let anchor = CGPoint(x: pinchStartCentroid.x - bounds.midX, y: pinchStartCentroid.y - bounds.midY)
            let now = g.location(in: self)
            offset = CGPoint(
                x: anchor.x - (anchor.x - pinchStartOffset.x) * ratio + (now.x - pinchStartCentroid.x),
                y: anchor.y - (anchor.y - pinchStartOffset.y) * ratio + (now.y - pinchStartCentroid.y))
            scale = newScale
            setNeedsLayout()
            onZoomChanged?(scale > 1.01)
        default:
            zooming = false
            if scale <= 1.01 { resetZoom() }
        }
    }

    // MARK: - Keyboard

    override var canBecomeFirstResponder: Bool { true }

    var hasText: Bool { true }

    func insertText(_ text: String) {
        if text == "\n" {
            onKey?("enter")
        } else {
            onText?(text)
        }
    }

    func deleteBackward() {
        onKey?("backspace")
    }

    var autocorrectionType: UITextAutocorrectionType = .no
    var autocapitalizationType: UITextAutocapitalizationType = .none
    var spellCheckingType: UITextSpellCheckingType = .no
    var smartQuotesType: UITextSmartQuotesType = .no
    var smartDashesType: UITextSmartDashesType = .no
    var smartInsertDeleteType: UITextSmartInsertDeleteType = .no
}

/// Hardware H.264 decoding and display (AVSampleBufferDisplayLayer): the PC's Annex-B access units are
/// turned into AVCC samples; SPS/PPS before each keyframe define the format.
final class H264VideoView: UIView {
    override class var layerClass: AnyClass { AVSampleBufferDisplayLayer.self }

    private var displayLayer: AVSampleBufferDisplayLayer { layer as! AVSampleBufferDisplayLayer }
    private var format: CMVideoFormatDescription?
    private var parameterSets: (sps: Data, pps: Data)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        displayLayer.videoGravity = .resize // the canvas already sizes this view to the picture
        backgroundColor = .black
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not used")
    }

    func enqueue(_ accessUnit: Data) -> Bool {
        let renderer = displayLayer.sampleBufferRenderer
        if renderer.status == .failed {
            renderer.flush()
            format = nil
            return false
        }
        let units = H264Stream.nalUnits(accessUnit)
        if let sps = units.first(where: { H264Stream.type(of: $0) == H264Stream.sps }),
           let pps = units.first(where: { H264Stream.type(of: $0) == H264Stream.pps }),
           parameterSets?.sps != sps || parameterSets?.pps != pps || format == nil {
            parameterSets = (sps, pps)
            format = Self.makeFormat(sps: sps, pps: pps)
        }
        let slices = units.filter { [H264Stream.slice, H264Stream.idr].contains(H264Stream.type(of: $0)) }
        guard let format, !slices.isEmpty else {
            // Nothing decodable yet (no SPS/PPS seen): ask for a keyframe.
            return format != nil
        }
        guard let sample = Self.makeSample(H264Stream.avcc(slices), format: format) else { return false }
        renderer.enqueue(sample)
        return true
    }

    private static func makeFormat(sps: Data, pps: Data) -> CMVideoFormatDescription? {
        var description: CMVideoFormatDescription?
        let status = sps.withUnsafeBytes { spsBuffer in
            pps.withUnsafeBytes { ppsBuffer in
                let pointers: [UnsafePointer<UInt8>] = [
                    spsBuffer.bindMemory(to: UInt8.self).baseAddress!,
                    ppsBuffer.bindMemory(to: UInt8.self).baseAddress!,
                ]
                let sizes = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault, parameterSetCount: 2, parameterSetPointers: pointers,
                    parameterSetSizes: sizes, nalUnitHeaderLength: 4, formatDescriptionOut: &description)
            }
        }
        return status == noErr ? description : nil
    }

    private static func makeSample(_ avcc: Data, format: CMVideoFormatDescription) -> CMSampleBuffer? {
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: avcc.count, blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil, offsetToData: 0, dataLength: avcc.count, flags: 0, blockBufferOut: &block) == noErr,
            let block else { return nil }
        let copied = avcc.withUnsafeBytes { raw in
            CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: avcc.count)
        }
        guard copied == noErr else { return nil }
        var sample: CMSampleBuffer?
        var size = avcc.count
        guard CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format, sampleCount: 1,
            sampleTimingEntryCount: 0, sampleTimingArray: nil, sampleSizeEntryCount: 1, sampleSizeArray: &size,
            sampleBufferOut: &sample) == noErr, let sample else { return nil }
        // Show each frame as soon as it is decoded (no clock: lowest delay).
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true) as? [NSMutableDictionary],
           let first = attachments.first {
            first[kCMSampleAttachmentKey_DisplayImmediately as NSString] = true
        }
        return sample
    }
}

