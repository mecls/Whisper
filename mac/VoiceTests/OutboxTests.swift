import XCTest
@testable import Voice
// G5: Outbox is @MainActor.
@MainActor
final class OutboxTests: XCTestCase {
    func testCapsAt200KeepingNewest() {
        let o = Outbox()
        for i in 0..<250 { o.add(OutboxEntry(clientId: UUID(), raw: "\(i)", injected: .raw, fallback: .offline, createdAt: Date(), mode: "clean", languageSetting: "auto", languageDetected: nil, app: nil, audioMs: 0, asrMs: 0)) }
        XCTAssertEqual(o.count, 200)
        XCTAssertEqual(o.drain().first?.raw, "50")
        XCTAssertEqual(o.count, 0)
    }
}
