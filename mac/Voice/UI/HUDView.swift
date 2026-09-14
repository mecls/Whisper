import SwiftUI

/// The bar: always on screen, as small as its contents allow.
///
/// It sizes itself — there are no fixed dimensions here and none in `HUDPanel`, which resizes the
/// window to this view's fitting size. That keeps it minimal at every state and, incidentally,
/// serves the click-through rule: the panel's frame is exactly the visible pill, so the region that
/// can swallow a click is never larger than what the user can see.
///
/// At rest it is a small outline lozenge and nothing else — no icon, no chrome around it. It exists
/// to say "the app is running" and to give the click somewhere to land; anything more is a
/// permanent object competing for attention with whatever the user is actually doing. The mic only
/// appears once there is something to say.
///
/// While listening it shows the waveform and nothing else. A moving waveform already says both
/// things a status line could — that it is recording, and that it is genuinely hearing you — so
/// "Listening" next to it is a word doing no work. The other stages keep their text, because
/// "Cleaning" or "Nothing heard" are not otherwise visible.
struct HUDView: View {
    @ObservedObject var model: HUDModel

    private enum Metric {
        /// The resting lozenge. Small enough to read as screen furniture rather than a control.
        static let idleWidth: CGFloat = 44
        static let idleHeight: CGFloat = 12
        static let mic: CGFloat = 22
        static let icon: CGFloat = 11
        /// The largest square that fits inside the mic circle (22 / √2 ≈ 15.6). Any bigger and the
        /// top and bottom of the spit mark's face hang outside the button.
        static let spit: CGFloat = 15
        static let text: CGFloat = 10.5
        static let bars = 20
        static let barWidth: CGFloat = 2.5
        static let barGap: CGFloat = 1.5
        static let barMax: CGFloat = 16
    }

    private var isIdle: Bool { if case .hidden = model.state { return true }; return false }
    private var isListening: Bool { if case .listening = model.state { return true }; return false }

    var body: some View {
        Group {
            if isIdle {
                idlePill
            } else {
                HStack(spacing: 7) {
                    micButton
                    if isListening { waveform } else { statusContent }
                }
                .padding(.horizontal, 9)
                .padding(.vertical, 5)
                .background(.ultraThinMaterial, in: Capsule())
                .overlay(Capsule().strokeBorder(.white.opacity(0.08)))
            }
        }
        .fixedSize()
    }

    /// The resting state: the whole lozenge is the click target, so there is no separate button to
    /// draw. Deliberately carries no padding or outer capsule — those would triple its footprint,
    /// and the panel is sized to this view, so every point of chrome is a point of screen it
    /// permanently occupies and a point that could swallow someone's click.
    private var idlePill: some View {
        Button(action: { model.onMicTap?() }) {
            Capsule()
                .fill(Color.secondary.opacity(0.22))
                .overlay(Capsule().strokeBorder(.white.opacity(0.22), lineWidth: 1))
                .frame(width: Metric.idleWidth, height: Metric.idleHeight)
        }
        .buttonStyle(.plain)
        .help(Strings.appName)
    }

    /// The one interactive element. Red while latched — which is also the only latched indicator
    /// the bar needs: with the waveform beside it, a badge reading "hands-free" is redundant.
    ///
    /// While listening it carries the spit mark instead of a microphone, its arcs following the
    /// voice. The newest few levels are pooled so one quiet sample between syllables does not blink
    /// the arcs off.
    private var micButton: some View {
        Button(action: { model.onMicTap?() }) {
            Group {
                if isListening {
                    SpitMark(level: model.levels.suffix(3).max() ?? 0)
                        .frame(width: Metric.spit, height: Metric.spit)
                } else {
                    Image(systemName: model.latched ? "stop.fill" : "mic.fill")
                        .font(.system(size: Metric.icon, weight: .semibold))
                }
            }
                .frame(width: Metric.mic, height: Metric.mic)
                .background(model.latched ? Color.red.opacity(0.9) : Color.secondary.opacity(0.18),
                            in: Circle())
                .foregroundStyle(model.latched ? .white : .primary)
        }
        .buttonStyle(.plain)
        .help(model.latched ? Strings.latched : Strings.appName)
    }

    /// Transcribing, cleaning, done, errors — states with nothing visual to speak for them.
    private var statusContent: some View {
        VStack(alignment: .leading, spacing: 1) {
            // Monospaced digits: without them "Transcribing 45 %" and "46 %" are different widths,
            // so the bar re-measured and re-centred on every progress tick — visible as a jitter
            // for the whole of a long transcription.
            Text(title).font(.system(size: Metric.text, weight: .medium)).monospacedDigit()
            if let live = model.liveText {
                // Live transcript. Display only: Whisper revises unconfirmed text, and a paste
                // cannot be taken back.
                Text(live).font(.system(size: Metric.text - 1)).lineLimit(1)
                    .truncationMode(.head).foregroundStyle(.secondary)
                    .frame(maxWidth: 220, alignment: .leading)
            } else if case .done(let preview, _) = model.state, let preview, Preferences.showTextInHUD {
                Text(preview).font(.system(size: Metric.text - 1)).lineLimit(1)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: 220, alignment: .leading)
            }
        }
        .allowsHitTesting(false)
    }

    private var title: String {
        switch model.state {
        case .hidden, .listening: ""
        case .transcribing(let p): p.map { "\(Strings.transcribing) \(Int($0 * 100)) %" } ?? Strings.transcribing
        case .cleaning: Strings.cleaning
        case .done(_, let via): via.map { "\(Strings.done) · \($0)" } ?? Strings.done
        case .message(let m): m
        case .modelLoading(let p): "\(Strings.modelLoading) \(Int(p * 100)) %"
        }
    }

    /// Always exactly `Metric.bars` bars, zero-padded at the front. A waveform that grew as the
    /// first samples arrived would make the whole bar resize during the first second of every
    /// dictation.
    private var waveform: some View {
        let recent = model.levels.suffix(Metric.bars)
        let padded = Array(repeating: Float(0), count: max(0, Metric.bars - recent.count)) + Array(recent)
        return HStack(spacing: Metric.barGap) {
            ForEach(Array(padded.enumerated()), id: \.offset) { _, l in
                RoundedRectangle(cornerRadius: 1)
                    .frame(width: Metric.barWidth,
                           height: max(2, CGFloat(Self.loudness(l)) * Metric.barMax))
            }
        }
        .frame(height: Metric.barMax)
        .foregroundStyle(model.latched ? Color.red.opacity(0.85) : Color.secondary)
        .allowsHitTesting(false)
    }

    /// A microphone level as 0...1, full at 0.025. Shared by the waveform and the spit mark's arcs
    /// so the two beside each other agree on what "loud" is.
    static func loudness(_ level: Float) -> Float { min(level * 40, 1) }
}
