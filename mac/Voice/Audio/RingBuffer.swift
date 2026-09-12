import Foundation
import os

/// Fixed-capacity sample buffer written from the audio thread, drained from the main thread.
/// Capacity is the 90 s cap: when full, writes are dropped and the recorder stops.
///
/// `@unchecked Sendable` is a claim about the lock, not a way around the warning it silences.
/// Every read and write of `storage` and `head` happens inside `lock`, and neither is ever handed
/// out: both readers copy into a fresh `Array` before returning, so no caller can hold a reference
/// into the storage after the lock is released. The compiler cannot check any of that, which is
/// what `@unchecked` says out loud — so `RingBufferTests` exercises the claim from several threads
/// at once rather than leaving it as an assertion nobody tests.
final class RingBuffer: @unchecked Sendable {
    private var storage: [Float]
    private var head = 0
    private let lock = OSAllocatedUnfairLock()

    init(capacity: Int) { storage = [Float](repeating: 0, count: capacity) }

    var count: Int { lock.withLock { head } }
    var isFull: Bool { lock.withLock { head >= storage.count } }

    /// Called on the realtime audio thread, which is why the body allocates nothing.
    ///
    /// `withLockUnchecked` rather than `withLock`: the latter takes a `@Sendable` closure, and
    /// `UnsafeBufferPointer` is not `Sendable`, so capturing `xs` is a warning today and an error
    /// under the Swift 6 language mode. Nothing actually escapes — the body runs synchronously on
    /// the caller's thread and is finished before `withLockUnchecked` returns — and this is the
    /// case the unchecked variant exists for. It is the same lock and the same call; only the
    /// compile-time `Sendable` requirement differs.
    ///
    /// The obvious alternative — copying `xs` into an `Array` so it becomes `Sendable` — would
    /// allocate on the audio thread, which is the one place allocation is not allowed. Silencing
    /// the warning that way would trade a false positive for a real glitch.
    func write(_ xs: UnsafeBufferPointer<Float>) {
        lock.withLockUnchecked {
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
