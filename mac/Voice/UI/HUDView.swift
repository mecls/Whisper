import SwiftUI

/// The bar: always on screen, as small as its contents allow.
///
/// It sizes itself — there are no fixed dimensions here and none in `HUDPanel`, which resizes the
/// window to this view's fitting size. That keeps it minimal at every state and, incidentally,
/// serves the click-through rule: the panel's frame is exactly the visible pill, so the region that
/// can swallow a click is never larger than what the user can see.
///
/// At rest it is just the mic in a pill: enough to say the app is running and to give the click a
/// target, and nothing more. Mode and language used to sit beside it and were removed — they are
/// already in the menu bar, and a permanent label restating settings that rarely change is exactly
/// the kind of thing that makes a persistent bar feel like clutter.
///
/// While listening it shows the waveform and nothing else. A moving waveform already says both
/// things a status line could — that it is recording, and that it is genuinely hearing you — so
/// "Listening" next to it is a word doing no work. The other stages keep their text, because
/// "Cleaning" or "Nothing heard" are not otherwise visible.
struct HUDView: View {
    @ObservedObject var model: HUDModel

    private enum Metric {
        static let mic: CGFloat = 22
        static let icon: CGFloat = 11
        static let text: CGFloat = 10.5
        static let bars = 20
        static let barWidth: CGFloat = 2.5
        static let barGap: CGFloat = 1.5
        static let barMax: CGFloat = 16
    }

    private var isIdle: Bool { if case .hidden = model.state { return true }; return false }
    private var isListening: Bool { if case .listening = model.state { return true }; return false }

    var body: some View {
        HStack(spacing: 7) {
            micButton
            if isListening {
                waveform
            } else if !isIdle {
                statusContent
            }
            // Idle renders nothing beside the mic — the pill itself is the signal.
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 5)
        .background(.ultraThinMaterial, in: Capsule())
        .overlay(Capsule().strokeBorder(.white.opacity(0.08)))
        .fixedSize()
    }

    /// The one interactive element. Red while latched — which is also the only latched indicator
    /// the bar needs: with the waveform beside it, a badge reading "hands-free" is redundant.
    private var micButton: some View {
        Button(action: { model.onMicTap?() }) {
            Image(systemName: model.latched ? "stop.fill" : "mic.fill")
                .font(.system(size: Metric.icon, weight: .semibold))
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
                           height: max(2, CGFloat(min(l * 40, 1)) * Metric.barMax))
            }
        }
        .frame(height: Metric.barMax)
        .foregroundStyle(model.latched ? Color.red.opacity(0.85) : Color.secondary)
        .allowsHitTesting(false)
    }
}
