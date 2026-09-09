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
}
