import Foundation
import os

/// Fixed-capacity sample buffer written from the audio thread, drained from the main thread.
/// Capacity is the 90 s cap: when full, writes are dropped and the recorder stops.
final class RingBuffer {
    private var storage: [Float]
    private var head = 0
    private let lock = OSAllocatedUnfairLock()

    init(capacity: Int) { storage = [Float](repeating: 0, count: capacity) }

    var count: Int { lock.withLock { head } }
    var isFull: Bool { lock.withLock { head >= storage.count } }

    func write(_ xs: UnsafeBufferPointer<Float>) {
        lock.withLock {
            let n = min(xs.count, storage.count - head)
            guard n > 0 else { return }
            storage.withUnsafeMutableBufferPointer { dst in
                dst.baseAddress!.advanced(by: head).update(from: xs.baseAddress!, count: n)
            }
            head += n
        }
    }

    /// Everything written so far, without consuming it.
    ///
    /// Streaming needs to read the buffer repeatedly while the dictation is still running, which
    /// `drain` cannot do — it resets `head`, and the next read would return only what arrived since.
    /// Safe to call from any thread, and deliberately *not* a purge: this buffer is already
    /// fixed-capacity, so unlike WhisperKit's own processor it has no unbounded growth to trim.
    func snapshot() -> [Float] {
        lock.withLock { Array(storage[0..<head]) }
    }

    func drain() -> [Float] {
        lock.withLock {
            let out = Array(storage[0..<head])
            head = 0
            return out
        }
    }
}
