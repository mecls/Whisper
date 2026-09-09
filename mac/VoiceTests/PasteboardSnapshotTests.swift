import XCTest
import AppKit
@testable import Voice

final class PasteboardSnapshotTests: XCTestCase {
    func testRoundTripsStringAndImage() {
        let pb = NSPasteboard.withUniqueName()
        pb.clearContents()
        let item = NSPasteboardItem()
        item.setString("hello", forType: .string)
        item.setData(Data([1, 2, 3]), forType: .png)
        pb.writeObjects([item])
        let snap = PasteboardSnapshot.capture(pb)!
        pb.clearContents(); pb.setString("replaced", forType: .string)
        snap.restore(to: pb)
        XCTAssertEqual(pb.string(forType: .string), "hello")
        XCTAssertEqual(pb.data(forType: .png), Data([1, 2, 3]))
        pb.releaseGlobally()
    }

    func testSkipsPromisedAndOversizedTypes() {
        let pb = NSPasteboard.withUniqueName()
        pb.clearContents()
        let item = NSPasteboardItem()
        item.setString("x", forType: .string)
        item.setData(Data(count: PasteboardSnapshot.maxBytes + 1), forType: .tiff)
        item.setData(Data(), forType: NSPasteboard.PasteboardType("com.apple.pasteboard.promised-file-url"))
        pb.writeObjects([item])
        let snap = PasteboardSnapshot.capture(pb)
        XCTAssertNil(snap) // promised content → not restorable, restore skipped entirely
        pb.releaseGlobally()
    }

    func testEmptyPasteboardIsRestorableAsEmpty() {
        let pb = NSPasteboard.withUniqueName()
        pb.clearContents()
        let snap = PasteboardSnapshot.capture(pb)!
        pb.setString("x", forType: .string)
        snap.restore(to: pb)
        XCTAssertNil(pb.string(forType: .string))
        pb.releaseGlobally()
    }
}
