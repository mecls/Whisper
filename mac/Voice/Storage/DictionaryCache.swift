import Foundation

/// Terms fed to Whisper's prompt. Cached to Application Support so the first dictation after launch already has them.
final class DictionaryCache {
    static let shared = DictionaryCache()
    private let url: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Voice", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base.appendingPathComponent("dictionary.json")
    }()
    private(set) var terms: [String]

    init() { terms = (try? JSONDecoder().decode([String].self, from: Data(contentsOf: url))) ?? [] }

    func update(_ terms: [String]) {
        self.terms = terms
        try? JSONEncoder().encode(terms).write(to: url, options: .atomic)
    }
}
