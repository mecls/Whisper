import AppKit
import CoreGraphics

enum InsertResult: Equatable { case pasted, pastedUnconfirmed, clipboardOnly }

/// Clipboard paste with a restore that waits for the target app to actually read the text.
final class TextInjector: NSObject, NSPasteboardItemDataProvider {
    static let transientType = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")
    static let concealedType = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")
    private static let readCeiling: TimeInterval = 1.5
    private static let restoreDelayAfterRead: TimeInterval = 0.1

    private var pending: (text: String, snapshot: PasteboardSnapshot?, completion: (InsertResult) -> Void)?
    private var ceiling: DispatchWorkItem?
    private var wasRead = false

    func insert(_ text: String, completion: @escaping (InsertResult) -> Void) {
        // A7: the reducer never issues a second `.insert` before `.inserted`, but stay safe — a stale
        // paste is closed (its snapshot restored) before a new one starts.
        if pending != nil { finish(.pastedUnconfirmed) }
        let pb = NSPasteboard.general
        if SecureInput.isActive {
            pb.prepareForNewContents(with: .currentHostOnly)
            pb.setString(text, forType: .string)
            completion(.clipboardOnly)
            return
        }
        let snapshot = PasteboardSnapshot.capture(pb)
        pending = (text, snapshot, completion)
        wasRead = false

        pb.prepareForNewContents(with: .currentHostOnly)   // never synced by Universal Clipboard
        let item = NSPasteboardItem()
        item.setDataProvider(self, forTypes: [.string])
        item.setData(Data(), forType: Self.transientType)     // clipboard managers: ignore this entry
        item.setData(Data(), forType: Self.concealedType)
        pb.writeObjects([item])

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.03) { Self.postCommandV() }

        let ceiling = DispatchWorkItem { [weak self] in self?.finish(.pastedUnconfirmed) }
        self.ceiling = ceiling
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.readCeiling, execute: ceiling)
    }

    // The target app is reading the clipboard right now: hand over the text, then restore shortly after.
    func pasteboard(_ pasteboard: NSPasteboard?, item: NSPasteboardItem, provideDataForType type: NSPasteboard.PasteboardType) {
        guard let pending else { return }
        item.setString(pending.text, forType: type)
        guard !wasRead else { return }
        wasRead = true
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.restoreDelayAfterRead) { [weak self] in self?.finish(.pasted) }
    }

    // Nothing is retained on the item side: `pending` (and its text/snapshot) lives only on `self`.
    func pasteboardFinishedWithDataProvider(_ pasteboard: NSPasteboard) {}

    private func finish(_ result: InsertResult) {
        guard let p = pending else { return }
        pending = nil
        ceiling?.cancel(); ceiling = nil
        if let snap = p.snapshot { snap.restore(to: NSPasteboard.general) }
        p.completion(result)
    }

    private static func postCommandV() {
        let src = CGEventSource(stateID: .combinedSessionState)
        let vKey: CGKeyCode = 9
        guard let down = CGEvent(keyboardEventSource: src, virtualKey: vKey, keyDown: true),
              let up = CGEvent(keyboardEventSource: src, virtualKey: vKey, keyDown: false) else { return }
        down.flags = .maskCommand; up.flags = .maskCommand
        down.post(tap: .cghidEventTap); up.post(tap: .cghidEventTap)
    }
}
