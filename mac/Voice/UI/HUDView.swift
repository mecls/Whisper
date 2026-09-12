import SwiftUI

/// The bar: always on screen, showing what Voice is doing and offering one control.
///
/// It has two sizes. Idle is small and quiet — a mic button and the current mode and language,
/// which is what sits at the bottom of the screen 99% of the time. Active widens to carry the
/// stage, the waveform and the hands-free indicator.
///
/// Only the mic button is hit-testable. Everything else is explicitly `.allowsHitTesting(false)`,
/// because the panel gave up `ignoresMouseEvents` to make that one button clickable and must not
/// swallow clicks meant for the window underneath.
struct HUDView: View {
    @ObservedObject var model: HUDModel

    static let idleSize = CGSize(width: 248, height: 40)
    static let activeSize = CGSize(width: 380, height: 60)

    static func size(for state: HUDState) -> CGSize {
        if case .hidden = state { return idleSize }
        return activeSize
    }

    private var isIdle: Bool { if case .hidden = model.state { return true }; return false }

    var body: some View {
        HStack(spacing: 10) {
            micButton
            if isIdle {
                idleContent
            } else {
                activeContent
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(width: Self.size(for: model.state).width,
               height: Self.size(for: model.state).height)
        .background(.ultraThinMaterial, in: Capsule())
        .overlay(Capsule().strokeBorder(.white.opacity(0.08)))
    }

    /// The one interactive element. Starts a hands-free session, or ends the running one — the
    /// same session concept the keyboard drives, so either can end what the other started.
    private var micButton: some View {
        Button(action: { model.onMicTap?() }) {
            Image(systemName: model.latched ? "stop.fill" : "mic.fill")
                .font(.system(size: 13, weight: .semibold))
                .frame(width: 26, height: 26)
                .background(model.latched ? Color.red.opacity(0.9) : Color.secondary.opacity(0.2),
                            in: Circle())
                .foregroundStyle(model.latched ? .white : .primary)
        }
        .buttonStyle(.plain)
        .help(model.latched ? Strings.latched : Strings.appName)
    }

    // MARK: - Idle

    private var idleContent: some View {
        HStack(spacing: 8) {
            Text(model.mode == "literal" ? Strings.modeLiteral : Strings.modeClean)
                .font(.system(size: 11, weight: .medium))
            Text("·").foregroundStyle(.tertiary)
            Text(languageLabel)
                .font(.system(size: 11, weight: .medium))
            Spacer(minLength: 0)
        }
        .foregroundStyle(.secondary)
        .allowsHitTesting(false)
    }

    private var languageLabel: String {
        switch model.language {
        case "pt": return Strings.langPt
        case "en": return Strings.langEn
        default: return Strings.langAuto
        }
    }

    // MARK: - Active

    private var activeContent: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(title).font(.system(size: 12, weight: .medium))
                    if model.latched { latchedBadge }
                }
                // Rule 15 carries over: a preview only with the preference on. On a bar that never
                // hides, the alternative is leaving the last thing you dictated on screen forever.
                if let live = model.liveText {
                    // Live transcript while speaking. Display only — it never reaches the target
                    // app, because Whisper revises unconfirmed text and a paste cannot be undone.
                    Text(live).font(.system(size: 10)).lineLimit(1)
                        .truncationMode(.head).foregroundStyle(.secondary)
                } else if case .done(let preview, _) = model.state, let preview, Preferences.showTextInHUD {
                    Text(preview).font(.system(size: 10)).lineLimit(1).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
            if case .listening = model.state { waveform }
        }
        .allowsHitTesting(false)
    }

    private var latchedBadge: some View {
        Text(Strings.latched)
            .font(.system(size: 9, weight: .semibold))
            .textCase(.uppercase)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Color.red.opacity(0.18), in: Capsule())
            .foregroundStyle(.red)
    }

    private var title: String {
        switch model.state {
        case .hidden: ""
        case .listening: Strings.listening
        case .transcribing(let p): p.map { "\(Strings.transcribing) \(Int($0 * 100)) %" } ?? Strings.transcribing
        case .cleaning: Strings.cleaning
        case .done(_, let via): via.map { "\(Strings.done) · \($0)" } ?? Strings.done
        case .message(let m): m
        case .modelLoading(let p): "\(Strings.modelLoading) \(Int(p * 100)) %"
        }
    }

    /// The one element that proves the microphone is genuinely hearing you. A flat waveform is what
    /// an afternoon of debugging an AirPods sample-rate mismatch would have shown in one second.
    private var waveform: some View {
        HStack(spacing: 2) {
            ForEach(Array(model.levels.suffix(20).enumerated()), id: \.offset) { _, l in
                RoundedRectangle(cornerRadius: 1)
                    .frame(width: 3, height: max(3, CGFloat(min(l * 40, 1)) * 24))
            }
        }
    }
}
