import XCTest
@testable import Voice

final class RingBufferTests: XCTestCase {
    func testWriteDrainPreservesOrder() {
        let rb = RingBuffer(capacity: 8)
        [1, 2, 3].withUnsafeBufferPointer { rb.write($0) }
        [4, 5].withUnsafeBufferPointer { rb.write($0) }
        XCTAssertEqual(rb.count, 5)
        XCTAssertEqual(rb.drain(), [1, 2, 3, 4, 5])
        XCTAssertEqual(rb.count, 0)
    }

    func testStopsAtCapacityInsteadOfOverwriting() {
        let rb = RingBuffer(capacity: 4)
        [1, 2, 3, 4, 5, 6].withUnsafeBufferPointer { rb.write($0) }
        XCTAssertTrue(rb.isFull)
        XCTAssertEqual(rb.drain(), [1, 2, 3, 4])
    }

    /// Exercises the `@unchecked Sendable` conformance instead of taking its word for it.
    ///
    /// `RingBuffer` tells the compiler it is safe across threads, and the compiler has no way to
    /// check that — which is the whole meaning of `@unchecked`. Without a test, the conformance is
    /// just a way of switching the warning off. The real thing it promises is that a reader never
    /// sees a sample the writer has not finished storing.
    ///
    /// Storage starts as zeroes and every writer writes `1`, so that promise has a shape a test can
    /// assert: `snapshot()` returns `storage[0..<head]`, and `head` only advances after the copy
    /// completes under the lock, so **every** sample a reader sees must already be `1`. A zero in
    /// there means a reader observed `head` ahead of the data it points at — the exact tear the
    /// lock exists to prevent.
    func testConcurrentWritesAreNeverVisibleBeforeTheyLand() {
        let frame = [Float](repeating: 1, count: 160)   // 10 ms at 16 kHz
        let writes = 400
        let rb = RingBuffer(capacity: frame.count * writes)

        // Every fifth iteration reads while the rest are mid-write, so snapshots land in the
        // middle of the writes rather than politely after them.
        DispatchQueue.concurrentPerform(iterations: writes + writes / 4) { i in
            if i % 5 == 0 {
                let seen = rb.snapshot()
                XCTAssertTrue(
                    seen.allSatisfy { $0 == 1 },
                    "snapshot() exposed a sample the writer had not finished storing — head moved ahead of the data"
                )
                XCTAssertLessThanOrEqual(seen.count, frame.count * writes)
            } else {
                frame.withUnsafeBufferPointer { rb.write($0) }
            }
        }

        // The writers are sized to fill it exactly, so a lost or double-counted update shows up
        // here as a count that is not the capacity.
        XCTAssertTrue(rb.isFull)
        XCTAssertEqual(rb.count, frame.count * writes)
        XCTAssertTrue(rb.drain().allSatisfy { $0 == 1 })
    }
}
