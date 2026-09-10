import XCTest
@testable import Voice

final class SmokeTests: XCTestCase {
    func testStringsExist() { XCTAssertFalse(Strings.appName.isEmpty) }
}
