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

    func drain() -> [Float] {
        lock.withLock {
            let out = Array(storage[0..<head])
            head = 0
            return out
        }
    }
}
