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
    @Environment(\.scenePhase) private var scenePhase

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
        .task { await stream.run() }
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
        if let image = stream.image {
            ScreenCanvas(image: image, sender: sender, typing: typing, resetToken: resetZoom, zoomed: $zoomed)
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
            if let message = sender.message ?? (stream.image != nil ? stream.message : nil) {
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

/// Pulls frames one at a time: the next request goes out when the previous frame arrived, so a slow
/// network lowers the frame rate instead of building a backlog. Unchanged pictures cost a few bytes.
@MainActor
@Observable
final class ScreenStream {
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
    }

    private let controller: PCController
    private var running = false
    private var hash: String?
    private var frameTimes: [Date] = []
    private var lastRtt: TimeInterval = 0
    private var lastBytes = 0
    var image: UIImage?
    var message: String?
    var preset: Preset = .normal {
        didSet { hash = nil }
    }

    init(controller: PCController) {
        self.controller = controller
    }

    var stats: String {
        let fps = frameTimes.count
        return "\(fps) к/с · \(Int(lastRtt * 1000)) мс · \(lastBytes / 1024) КБ"
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
        while running {
            let started = Date()
            var payload: [String: Any] = ["maxWidth": preset.maxWidth, "quality": preset.quality]
            if let hash { payload["known"] = hash }
            do {
                let frame = try await controller.fetch("screen.frame", payload: payload, timeout: 15)
                lastRtt = Date().timeIntervalSince(started)
                hash = frame["hash"] as? String
                if let base64 = frame["jpeg"] as? String, let data = Data(base64Encoded: base64) {
                    lastBytes = data.count
                    if let decoded = await Task.detached(operation: { UIImage(data: data)?.preparingForDisplay() }).value {
                        image = decoded
                    }
                    countFrame()
                } else {
                    // Nothing changed on the PC: ask again shortly.
                    try? await Task.sleep(nanoseconds: 60_000_000)
                }
                if message != nil { message = nil }
            } catch let error as NexusError where error.code == "screen.notStarted" && running {
                // The session ended on the PC (idle or the tray restarted): start again.
                _ = try? await controller.fetch("screen.start", timeout: 125)
            } catch {
                message = Self.describe(error, controller: controller)
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                if case .connection? = error as? NexusError { running = false }
            }
        }
    }

    func stop() {
        guard running else { return }
        running = false
        Task { _ = try? await controller.fetch("screen.stop") }
    }

    private func countFrame() {
        let now = Date()
        frameTimes.append(now)
        frameTimes.removeAll { now.timeIntervalSince($0) > 1 }
    }

    static func describe(_ error: Error, controller: PCController) -> String {
        let nexus = error as? NexusError
        switch nexus?.code {
        case "auth.permissionDenied":
            return "Показ экрана не разрешён для этого iPhone. На ПК: значок Nexus PC → «Устройства и разрешения…» → отметьте «Показ экрана» и «Тачпад и клавиатура»."
        case "session.unavailable":
            return "На ПК не запущен Nexus PC в трее — экран показывается через него."
        case "screen.denied", "screen.blocked":
            if case let .server(_, text)? = nexus { return text }
            return "Показ экрана запрещён на ПК."
        default:
            if case .timeout? = nexus { return "ПК не ответил: возможно, окно разрешения на ПК ещё открыто." }
            if case let .server(_, text)? = nexus { return text }
            return (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}

/// The picture and its touch layer in one UIKit view, so taps map to the PC exactly even when zoomed.
struct ScreenCanvas: UIViewRepresentable {
    let image: UIImage
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
        view.image = image
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

    var image: UIImage? {
        didSet {
            guard image !== oldValue else { return }
            let resized = oldValue?.size != image?.size
            imageView.image = image
            if resized { setNeedsLayout() }
        }
    }

    private let imageView = UIImageView()
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

    /// The picture fitted into the view at zoom 1.
    private var fittedRect: CGRect {
        guard let size = image?.size, size.width > 0, size.height > 0, bounds.width > 0 else { return bounds }
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
