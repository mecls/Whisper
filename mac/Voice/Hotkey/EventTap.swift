import CoreGraphics
import Foundation

/// Listen-only session event tap. Recreated when the mask changes (a tap's mask is fixed at creation).
final class EventTap {
    private var port: CFMachPort?
    private var source: CFRunLoopSource?
    var onEvent: ((CGEventType, CGEvent) -> Void)?

    @discardableResult
    func start(mask: CGEventMask) -> Bool {
        stop()
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let port = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                let me = Unmanaged<EventTap>.fromOpaque(refcon!).takeUnretainedValue()
                if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                    // The system disables a tap after one slow callback; without this the hotkey silently dies.
                    if let p = me.port { CGEvent.tapEnable(tap: p, enable: true) }
                    return Unmanaged.passUnretained(event)
                }
                me.onEvent?(type, event)
                return Unmanaged.passUnretained(event)
            },
            userInfo: refcon) else { return false }
        self.port = port
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, port, 0)
        self.source = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: port, enable: true)
        return true
    }

    func stop() {
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let port { CGEvent.tapEnable(tap: port, enable: false) }
        source = nil; port = nil
    }

    deinit { stop() }
}
