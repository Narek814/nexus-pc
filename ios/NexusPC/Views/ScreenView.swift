import NexusKit
import SwiftUI
import UIKit

/// Remote screen (P5): JPEG frames of the PC's main monitor over the existing encrypted connection,
/// with touch control. The PC asks its owner once per device; a notice shows while someone watches.
struct ScreenView: View {
    let controller: PCController

    @State private var stream: ScreenStream
    @State private var sender: InputSender
    @State private var typing = false
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
                if let image = stream.image {
                    Image(uiImage: image)
                        .resizable()
                        .interpolation(.medium)
                        .aspectRatio(contentMode: .fit)
                        .overlay {
                            ScreenTouchSurface(sender: sender, typing: typing)
                        }
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
            controls
        }
        .background(Theme.background)
        .navigationTitle("Экран ПК")
        .navigationBarTitleDisplayMode(.inline)
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
        .onDisappear { stream.stop() }
        .onChange(of: scenePhase) { _, phase in
            if phase != .active { stream.stop() }
        }
    }

    private var controls: some View {
        VStack(spacing: 6) {
            if let message = sender.message ?? (stream.image != nil ? stream.message : nil) {
                Text(message).font(.caption2).foregroundStyle(.orange).lineLimit(3)
            }
            HStack(spacing: 10) {
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
            Text("Касание — клик · удержание — правый клик · вести пальцем — перетаскивание · два пальца — прокрутка")
                .font(.caption2)
                .foregroundStyle(Theme.textMuted)
                .lineLimit(2)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
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

/// Touch layer over the picture: positions are mapped to the PC's primary monitor (0...65535).
struct ScreenTouchSurface: UIViewRepresentable {
    let sender: InputSender
    let typing: Bool

    func makeUIView(context: Context) -> ScreenTouchView {
        let view = ScreenTouchView()
        configure(view)
        return view
    }

    func updateUIView(_ view: ScreenTouchView, context: Context) {
        configure(view)
        if typing, !view.isFirstResponder {
            view.becomeFirstResponder()
        } else if !typing, view.isFirstResponder {
            view.resignFirstResponder()
        }
    }

    private func configure(_ view: ScreenTouchView) {
        let sender = sender
        view.onPoint = { x, y in sender.send(.moveTo(x: x, y: y)) }
        view.onButton = { button, action in sender.send(.button(button, action: action)) }
        view.onScroll = { dx, dy in sender.scroll(dx: dx, dy: dy) }
        view.onText = { text in sender.send(.text(text)) }
        view.onKey = { key in sender.send(.key(key)) }
    }
}

final class ScreenTouchView: UIView, UIKeyInput {
    var onPoint: ((Int, Int) -> Void)?
    var onButton: ((String, String) -> Void)?
    var onScroll: ((Double, Double) -> Void)?
    var onText: ((String) -> Void)?
    var onKey: ((String) -> Void)?

    private var lastScroll = CGPoint.zero
    private let haptic = UIImpactFeedbackGenerator(style: .light)

    override init(frame: CGRect) {
        super.init(frame: frame)
        isMultipleTouchEnabled = true
        backgroundColor = .clear

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
        addGestureRecognizer(scroll)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not used")
    }

    private func point(_ location: CGPoint) {
        guard bounds.width > 0, bounds.height > 0 else { return }
        let x = Int((min(max(location.x / bounds.width, 0), 1) * 65535).rounded())
        let y = Int((min(max(location.y / bounds.height, 0), 1) * 65535).rounded())
        onPoint?(x, y)
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
