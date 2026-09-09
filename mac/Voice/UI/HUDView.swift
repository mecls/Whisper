import SwiftUI

struct HUDView: View {
    @ObservedObject var model: HUDModel

    var body: some View {
        HStack(spacing: 12) {
            icon
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .medium))
                if case .done(let preview) = model.state, let preview, Preferences.showTextInHUD {
                    Text(preview).font(.system(size: 11)).lineLimit(1).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
            if case .listening = model.state { waveform }
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .frame(width: 320, height: 64)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 14))
    }

    private var title: String {
        switch model.state {
        case .hidden: ""
        case .listening: Strings.listening
        case .transcribing(let p): p.map { "\(Strings.transcribing) \(Int($0 * 100)) %" } ?? Strings.transcribing
        case .cleaning: Strings.cleaning
        case .done: Strings.done
        case .message(let m): m
        case .modelLoading(let p): "\(Strings.modelLoading) \(Int(p * 100)) %"
        }
    }

    private var icon: some View {
        Image(systemName: {
            switch model.state {
            case .listening: "mic.fill"
            case .transcribing, .cleaning, .modelLoading: "waveform"
            case .done: "checkmark.circle.fill"
            case .message: "exclamationmark.circle"
            case .hidden: "mic"
            }
        }()).font(.system(size: 18))
    }

    private var waveform: some View {
        HStack(spacing: 2) {
            ForEach(Array(model.levels.suffix(24).enumerated()), id: \.offset) { _, l in
                RoundedRectangle(cornerRadius: 1).frame(width: 3, height: max(3, CGFloat(min(l * 40, 1)) * 28))
            }
        }
    }
}
