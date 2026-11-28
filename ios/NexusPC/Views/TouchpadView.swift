import NexusKit
import SwiftUI
import UIKit

/// Remote touchpad and keyboard (P5). Needs the `input` permission, which the PC owner grants explicitly.
struct TouchpadView: View {
    let controller: PCController

    @State private var sender: InputSender
    @State private var typing = false
    @AppStorage("touchpad.speed") private var speed = 1.5
    @AppStorage("touchpad.naturalScroll") private var naturalScroll = true

    init(controller: PCController) {
        self.controller = controller
        _sender = State(initialValue: InputSender(controller: controller))
    }

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()
            VStack(spacing: 12) {
                if let message = sender.message {
                    Text(message)
                        .font(.footnote)
                        .foregroundStyle(.orange)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
                pad
                clickButtons
                keysRow
                mediaRow
                settings
            }
            .padding(16)
        }
        .navigationTitle("Тачпад")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    typing.toggle()
                } label: {
                    Image(systemName: typing ? "keyboard.chevron.compact.down" : "keyboard")
                }
                .tint(Theme.cyan)
                .accessibilityLabel(typing ? "Скрыть клавиатуру" : "Клавиатура")
            }
        }
    }

    private var pad: some View {
        ZStack {
            RoundedRectangle(cornerRadius: Theme.cornerRadius, style: .continuous)
                .fill(Theme.surface)
                .overlay(
                    RoundedRectangle(cornerRadius: Theme.cornerRadius, style: .continuous)
                        .strokeBorder(Theme.accent, lineWidth: 1)
                        .opacity(0.6)
                )
            VStack(spacing: 6) {
                Image(systemName: "hand.point.up.left").font(.title2)
                Text("Один палец — курсор, касание — клик\nДва пальца — прокрутка, касание двумя — правый клик\nУдержать и вести — перетаскивание")
                    .font(.caption2)
                    .multilineTextAlignment(.center)
            }
            .foregroundStyle(Theme.textMuted.opacity(0.6))
            .allowsHitTesting(false)
            TouchpadSurface(
                speed: speed,
                naturalScroll: naturalScroll,
                typing: typing,
                sender: sender
            )
        }
        .frame(maxHeight: .infinity)
    }

    private var clickButtons: some View {
        HStack(spacing: 10) {
            padButton("Левая") { sender.send(.button("left", action: "click")) }
            padButton("Правая") { sender.send(.button("right", action: "click")) }
        }
        .frame(height: 48)
    }

    private func padButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button {
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            action()
        } label: {
            Text(title)
                .font(.subheadline.weight(.semibold))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Theme.surface, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
        .buttonStyle(.plain)
        .foregroundStyle(Theme.textPrimary)
    }

    private var keysRow: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                chip("Esc", .key("escape"))
                chip("Tab", .key("tab"))
                chip("Enter", .key("enter"))
                chip("⌫", .key("backspace"))
                chip("Del", .key("delete"))
                chip("←", .key("left"))
                chip("↑", .key("up"))
                chip("↓", .key("down"))
                chip("→", .key("right"))
                chip("Win", .key("win"))
                chip("Ctrl+C", .key("c", modifiers: ["ctrl"]))
                chip("Ctrl+V", .key("v", modifiers: ["ctrl"]))
                chip("Ctrl+Z", .key("z", modifiers: ["ctrl"]))
                chip("Alt+Tab", .key("tab", modifiers: ["alt"]))
                chip("Win+D", .key("d", modifiers: ["win"]))
                chip("Alt+F4", .key("f4", modifiers: ["alt"]))
            }
        }
    }

    private func chip(_ title: String, _ command: InputCommand) -> some View {
        Button {
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            sender.send(command)
        } label: {
            Text(title)
                .font(.footnote.weight(.semibold))
                .padding(.horizontal, 12)
                .padding(.vertical, 9)
                .background(Theme.surface, in: Capsule())
                .overlay(Capsule().strokeBorder(Theme.violet.opacity(0.5), lineWidth: 1))
        }
        .buttonStyle(.plain)
        .foregroundStyle(Theme.textPrimary)
    }

    private var mediaRow: some View {
        HStack {
            media("backward.fill", "previous", "Предыдущий трек")
            media("playpause.fill", "playPause", "Пауза или воспроизведение")
            media("forward.fill", "next", "Следующий трек")
            Spacer()
            media("speaker.minus.fill", "volumeDown", "Тише")
            media("speaker.slash.fill", "mute", "Без звука")
            media("speaker.plus.fill", "volumeUp", "Громче")
        }
    }

    private func media(_ icon: String, _ action: String, _ label: String) -> some View {
        Button {
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            sender.send(.media(action))
        } label: {
            Image(systemName: icon)
                .font(.body)
                .frame(width: 44, height: 40)
                .background(Theme.surface, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        }
        .buttonStyle(.plain)
        .foregroundStyle(Theme.cyan)
        .accessibilityLabel(label)
    }

    private var settings: some View {
        HStack(spacing: 12) {
            Image(systemName: "tortoise").foregroundStyle(Theme.textMuted)
            Slider(value: $speed, in: 0.5...4).tint(Theme.violet)
            Image(systemName: "hare").foregroundStyle(Theme.textMuted)
            Toggle("Естеств.", isOn: $naturalScroll)
                .toggleStyle(.button)
                .font(.caption)
                .tint(Theme.violet)
                .accessibilityLabel("Естественная прокрутка")
        }
    }
}

/// Sends input one request at a time: moves made while a request is in flight are merged
/// (InputQueue), so a slow network delays the cursor instead of building up a backlog.
@MainActor
@Observable
final class InputSender {
    private let controller: PCController
    private var queue = InputQueue()
    private var sending = false
    private var lastSent: Date?
    var message: String?

    init(controller: PCController) {
        self.controller = controller
    }

    func move(dx: Double, dy: Double) {
        queue.move(dx: dx, dy: dy)
        pump()
    }

    func scroll(dx: Double, dy: Double) {
        queue.scroll(dx: dx, dy: dy)
        pump()
    }

    func send(_ command: InputCommand) {
        queue.send(command)
        pump()
    }

    private func pump() {
        guard !sending else { return }
        sending = true
        Task { await drain() }
    }

    private func drain() async {
        while let next = queue.next() {
            // At most ~80 requests a second (the agent allows 100): moves merge while we wait.
            if let last = lastSent, Date().timeIntervalSince(last) < 0.012 {
                try? await Task.sleep(nanoseconds: 12_000_000)
            }
            lastSent = Date()
            do {
                _ = try await controller.fetch(next.type, payload: next.payload)
                if message != nil { message = nil }
            } catch {
                queue.clear()
                message = describe(error)
            }
        }
        sending = false
    }

    private func describe(_ error: Error) -> String {
        switch (error as? NexusError)?.code {
        case "auth.permissionDenied":
            let id = controller.pc?.deviceId.uuidString.lowercased() ?? "<deviceId>"
            return "Тачпад не разрешён для этого iPhone. На ПК в PowerShell от имени администратора:\n& \"C:\\Program Files\\NexusPC\\Agent\\Nexus.Agent.exe\" devices permissions \(id) power,apps,monitoring,rgb,input"
        case "session.unavailable":
            return "На ПК не запущен Nexus PC в трее — тачпад работает через него."
        case "input.blocked":
            if case let .server(_, text)? = error as? NexusError {
                return text
            }
            return "Windows не принимает ввод."
        default:
            return (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}

/// UIKit gestures: SwiftUI has no two-finger pan.
struct TouchpadSurface: UIViewRepresentable {
    let speed: Double
    let naturalScroll: Bool
    let typing: Bool
    let sender: InputSender

    func makeUIView(context: Context) -> TouchpadUIView {
        let view = TouchpadUIView()
        configure(view)
        return view
    }

    func updateUIView(_ view: TouchpadUIView, context: Context) {
        configure(view)
        if typing, !view.isFirstResponder {
            view.becomeFirstResponder()
        } else if !typing, view.isFirstResponder {
            view.resignFirstResponder()
        }
    }

    private func configure(_ view: TouchpadUIView) {
        let speed = speed
        let natural = naturalScroll
        let sender = sender
        view.onMove = { delta, velocity in
            // Faster finger → proportionally further, like a laptop touchpad.
            let gain = speed * (1 + min(Double(velocity) / 900, 2))
            sender.move(dx: Double(delta.x) * gain, dy: Double(delta.y) * gain)
        }
        view.onScroll = { delta in
            // 120 wheel units = one notch; ~40 pt of finger travel per notch.
            let sign: Double = natural ? 1 : -1
            sender.scroll(dx: -Double(delta.x) * 3 * sign, dy: Double(delta.y) * 3 * sign)
        }
        view.onTap = { sender.send(.button("left", action: "click")) }
        view.onRightTap = { sender.send(.button("right", action: "click")) }
        view.onDrag = { down in sender.send(.button("left", action: down ? "down" : "up")) }
        view.onText = { text in sender.send(.text(text)) }
        view.onKey = { key in sender.send(.key(key)) }
    }
}

final class TouchpadUIView: LiveKeyInputView {
    var onMove: ((CGPoint, CGFloat) -> Void)?
    var onScroll: ((CGPoint) -> Void)?
    var onTap: (() -> Void)?
    var onRightTap: (() -> Void)?
    var onDrag: ((Bool) -> Void)?

    private var lastMove = CGPoint.zero
    private var lastScroll = CGPoint.zero
    private var lastDrag = CGPoint.zero
    private let haptic = UIImpactFeedbackGenerator(style: .light)

    override init(frame: CGRect) {
        super.init(frame: frame)
        isMultipleTouchEnabled = true
        backgroundColor = .clear

        let move = UIPanGestureRecognizer(target: self, action: #selector(handleMove(_:)))
        move.minimumNumberOfTouches = 1
        move.maximumNumberOfTouches = 1
        addGestureRecognizer(move)

        let scroll = UIPanGestureRecognizer(target: self, action: #selector(handleScroll(_:)))
        scroll.minimumNumberOfTouches = 2
        scroll.maximumNumberOfTouches = 2
        addGestureRecognizer(scroll)

        let tap = UITapGestureRecognizer(target: self, action: #selector(handleTap))
        addGestureRecognizer(tap)

        let rightTap = UITapGestureRecognizer(target: self, action: #selector(handleRightTap))
        rightTap.numberOfTouchesRequired = 2
        addGestureRecognizer(rightTap)

        let drag = UILongPressGestureRecognizer(target: self, action: #selector(handleDrag(_:)))
        drag.minimumPressDuration = 0.35
        addGestureRecognizer(drag)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not used")
    }

    @objc private func handleMove(_ g: UIPanGestureRecognizer) {
        let t = g.translation(in: self)
        switch g.state {
        case .began:
            lastMove = t
        case .changed:
            let v = g.velocity(in: self)
            onMove?(CGPoint(x: t.x - lastMove.x, y: t.y - lastMove.y), hypot(v.x, v.y))
            lastMove = t
        default:
            lastMove = .zero
        }
    }

    @objc private func handleScroll(_ g: UIPanGestureRecognizer) {
        let t = g.translation(in: self)
        switch g.state {
        case .began:
            lastScroll = t
        case .changed:
            onScroll?(CGPoint(x: t.x - lastScroll.x, y: t.y - lastScroll.y))
            lastScroll = t
        default:
            lastScroll = .zero
        }
    }

    @objc private func handleTap() {
        haptic.impactOccurred()
        onTap?()
    }

    @objc private func handleRightTap() {
        haptic.impactOccurred()
        onRightTap?()
    }

    @objc private func handleDrag(_ g: UILongPressGestureRecognizer) {
        let p = g.location(in: self)
        switch g.state {
        case .began:
            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
            lastDrag = p
            onDrag?(true)
        case .changed:
            onMove?(CGPoint(x: p.x - lastDrag.x, y: p.y - lastDrag.y), 0)
            lastDrag = p
        default:
            onDrag?(false)
        }
    }
}

/// Live typing field (UIKeyInput): every key goes straight to the PC. Shared by the touchpad
/// (via `TouchpadUIView`) and the Keyboard screen; first responder while `typing` is true.
struct LiveTypingField: UIViewRepresentable {
    let typing: Bool
    let sender: InputSender

    func makeUIView(context: Context) -> LiveKeyInputView {
        let view = LiveKeyInputView()
        configure(view)
        return view
    }

    func updateUIView(_ view: LiveKeyInputView, context: Context) {
        configure(view)
        if typing, !view.isFirstResponder {
            view.becomeFirstResponder()
        } else if !typing, view.isFirstResponder {
            view.resignFirstResponder()
        }
    }

    private func configure(_ view: LiveKeyInputView) {
        let sender = sender
        view.onText = { text in sender.send(.text(text)) }
        view.onKey = { key in sender.send(.key(key)) }
    }
}

class LiveKeyInputView: UIView, UIKeyInput {
    var onText: ((String) -> Void)?
    var onKey: ((String) -> Void)?

    // MARK: - Live typing (UIKeyInput): every key goes straight to the PC.

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
    var keyboardType: UIKeyboardType = .default
    var returnKeyType: UIReturnKeyType = .default
}
