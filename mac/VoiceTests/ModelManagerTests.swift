import XCTest
@testable import Voice

/// Always-on (no model, no network): `isDownloaded` must treat a folder as downloaded only when it
/// is complete, not merely present — an interrupted download must not be reported as ready.
final class ModelManagerTests: XCTestCase {
    private let manager = ModelManager()
    private var tempBase: URL!

    override func setUpWithError() throws {
        tempBase = FileManager.default.temporaryDirectory.appendingPathComponent("ModelManagerTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: tempBase, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: tempBase)
    }

    func testIsDownloadedFalseWhenFolderMissing() {
        XCTAssertFalse(manager.isDownloaded("missing-model", base: tempBase))
    }

    func testIsDownloadedFalseWhenFolderEmpty() throws {
        let folder = manager.folder(for: "empty-model", base: tempBase)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertFalse(manager.isDownloaded("empty-model", base: tempBase))
    }

    func testIsDownloadedFalseWhenOnlyConfigPresent() throws {
        let folder = manager.folder(for: "partial-model", base: tempBase)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: folder.appendingPathComponent("config.json"))
        XCTAssertFalse(manager.isDownloaded("partial-model", base: tempBase))
    }

    func testIsDownloadedTrueWhenAllRequiredEntriesPresent() throws {
        let folder = manager.folder(for: "complete-model", base: tempBase)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for entry in ModelManager.requiredEntries {
            if entry.hasSuffix(".mlmodelc") {
                try FileManager.default.createDirectory(at: folder.appendingPathComponent(entry), withIntermediateDirectories: true)
            } else {
                try Data("{}".utf8).write(to: folder.appendingPathComponent(entry))
            }
        }
        XCTAssertTrue(manager.isDownloaded("complete-model", base: tempBase))
    }
}
