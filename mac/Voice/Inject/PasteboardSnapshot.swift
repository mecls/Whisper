import AppKit

/// A restorable copy of the pasteboard. Whitelisted types only; promised (lazy) content makes the
/// whole snapshot non-restorable, in which case the injector skips the restore rather than corrupt it.
struct PasteboardSnapshot {
    static let allowedTypes: [NSPasteboard.PasteboardType] = [.string, .rtf, .html, .URL, .fileURL, .png, .tiff]
    static let maxBytes = 5_000_000

    private let items: [[NSPasteboard.PasteboardType: Data]]

    static func capture(_ pb: NSPasteboard) -> PasteboardSnapshot? {
        var out: [[NSPasteboard.PasteboardType: Data]] = []
        for item in pb.pasteboardItems ?? [] {
            if item.types.contains(where: { $0.rawValue.contains("promise") }) { return nil }
            var copy: [NSPasteboard.PasteboardType: Data] = [:]
            for t in item.types where allowedTypes.contains(t) {
                guard let d = item.data(forType: t), d.count <= maxBytes else { continue }
                copy[t] = d
            }
            if !copy.isEmpty { out.append(copy) }
        }
        return PasteboardSnapshot(items: out)
    }

    func restore(to pb: NSPasteboard) {
        pb.clearContents()
        guard !items.isEmpty else { return }
        let objs: [NSPasteboardItem] = items.map { dict in
            let it = NSPasteboardItem()
            for (t, d) in dict { it.setData(d, forType: t) }
            return it
        }
        pb.writeObjects(objs)
    }
}
