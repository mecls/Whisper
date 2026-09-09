import XCTest
@testable import Voice
final class BudgetTests: XCTestCase {
    func testClampAndScale() {
        XCTAssertEqual(Budget.ms(rawChars: 0), 3500)
        XCTAssertEqual(Budget.ms(rawChars: 100), 3700)
        XCTAssertEqual(Budget.ms(rawChars: 2000), 15000)
    }
}
